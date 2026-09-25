using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using libplctag.NativeImport;

namespace Jst.PlcFinder.Core;

public interface ITagReader : IDisposable
{
    Task<FieldRead[]> ReadAsync(PlcRow row, TagSettings settings, CancellationToken token);
}

public static class ValueDecoder
{
    public static string Decode(ReadOnlySpan<byte> data, byte type, string format)
    {
        if (format == "STRING")
        {
            if (type != 0xA0 || data.Length < 4) throw new FormatException("Expected Logix STRING structure. Check the tag type.");
            int length = BinaryPrimitives.ReadInt32LittleEndian(data);
            if (length < 0 || length > data.Length - 4 || length > 4096) throw new FormatException("Unsupported STRING layout or invalid length.");
            var value = data.Slice(4, length);
            foreach (byte b in value) if (b < 32 && b != 9) throw new FormatException("STRING contains unsupported control characters.");
            return Encoding.Latin1.GetString(value);
        }
        return (format, type, data.Length) switch
        {
            ("DINT", 0xC4, 4) => BinaryPrimitives.ReadInt32LittleEndian(data).ToString(CultureInfo.InvariantCulture),
            ("LINT", 0xC5, 8) => BinaryPrimitives.ReadInt64LittleEndian(data).ToString(CultureInfo.InvariantCulture),
            ("INT", 0xC3, 2) => BinaryPrimitives.ReadInt16LittleEndian(data).ToString(CultureInfo.InvariantCulture),
            ("REAL", 0xCA, 4) => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data)).ToString(CultureInfo.InvariantCulture),
            _ => throw new FormatException($"Actual PLC type 0x{type:X2} / {data.Length} bytes does not match {format}.")
        };
    }
}

public sealed class TagReader : ITagReader
{
    private readonly ConcurrentDictionary<string, int> handles = new();
    public static string Version => $"{plctag.plc_tag_get_int_attribute(0, "version_major", 0)}.{plctag.plc_tag_get_int_attribute(0, "version_minor", 0)}.{plctag.plc_tag_get_int_attribute(0, "version_patch", 0)}";
    public static void VerifyNativeLibrary()
    {
        int status = plctag.plc_tag_check_lib_version(2, 5, 0);
        if (status != 0) throw new InvalidOperationException($"PLC library could not initialize: {status}");
    }

    public async Task<FieldRead[]> ReadAsync(PlcRow row, TagSettings settings, CancellationToken token)
    {
        var result = new FieldRead[4];
        for (int i = 0; i < 4; i++)
        {
            token.ThrowIfCancellationRequested();
            string name = settings.FullName(i);
            string key = $"{row.Key}|{name}";
            try
            {
                if (!handles.TryGetValue(key, out int handle))
                {
                    // libplctag 2.6.0 requires a nonempty path attribute for Logix. Its CIP
                    // path parser ignores separators: ',' encodes an empty/direct route.
                    // Connected messaging appends the Message Router object to this route,
                    // allowing direct Forward Open without assuming a backplane slot.
                    // Keep covered by the actual-native-library loopback integration test.
                    string path = row.Route.Length == 0 ? "," : row.Route;
                    string attributes = $"protocol=ab_eip&gateway={row.Ip}&path={path}&plc=ControlLogix&elem_count=1&name={name}&use_connected_msg=1&allow_packing=1";
                    handle = plctag.plc_tag_create(attributes, 0);
                    Check(handle < 0 ? handle : 0);
                    handles[key] = handle;
                    await WaitAsync(handle, plctag.plc_tag_status(handle), token);
                }
                else
                {
                    // Logix handle creation already performs its initial read in
                    // libplctag 2.6.0. Only existing handles need an explicit refresh.
                    await WaitAsync(handle, plctag.plc_tag_read(handle, 0), token);
                }
                var type = new byte[32];
                int typeResult = plctag.plc_tag_get_byte_array_attribute(handle, "raw_tag_type_bytes", type, type.Length);
                Check(typeResult < 0 ? typeResult : 0);
                if (i == 3)
                {
                    if (type[0] is not (0xC2 or 0xC3 or 0xC4 or 0xC5 or 0xD3))
                        throw new FormatException("Simulation parent must be an integer/bit field; check STATION.OO.2.");
                    int bit = plctag.plc_tag_get_bit(handle, 0); // Bit-tag suffix selects bit 2; accessor offset is ignored by libplctag.
                    Check(bit < 0 ? bit : 0);
                    result[i] = new((bit == 1 ^ settings.InvertSimulation) ? "ON" : "Off", null);
                }
                else
                {
                    int size = plctag.plc_tag_get_size(handle);
                    if (size < 1 || size > 8192) throw new FormatException($"Unsupported tag size: {size} bytes.");
                    var bytes = new byte[size];
                    Check(plctag.plc_tag_get_raw_bytes(handle, 0, bytes, size));
                    result[i] = new(ValueDecoder.Decode(bytes, type[0], i == 0 ? "STRING" : i == 1 ? settings.SoftwareFormat : settings.GemFormat), null);
                }
            }
            catch (OperationCanceledException) { Remove(key); throw; }
            catch (Exception ex) when (ex is PlcReadException or FormatException)
            {
                result[i] = FieldRead.Failed(ex.Message);
                Remove(key);
            }
        }
        return result;
    }

    public async Task<FieldRead[]> ReadDeveloperTrackingAsync(PlcRow row, DeveloperTrackingSettings settings, CancellationToken token)
    {
        var result = new FieldRead[5];
        for (int i = 0; i < result.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            try { result[i] = new(await ReadLogixStringAsync(row, settings.Tags[i], token), null); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is PlcReadException or FormatException) { result[i] = FieldRead.Failed(ex.Message); }
        }
        return result;
    }

    public async Task WriteDeveloperTrackingAsync(PlcRow row, DeveloperTrackingSettings settings,
        IReadOnlyDictionary<int, string> changes, CancellationToken token)
    {
        if (settings.Tags.Distinct(StringComparer.OrdinalIgnoreCase).Count() != settings.Tags.Length)
            throw new FormatException("Developer tracking fields must use distinct PLC tags.");
        // Validate every value before the first write, including the automatic date.
        foreach (var change in changes)
        {
            if (change.Key < 0 || change.Key >= settings.Tags.Length) throw new ArgumentOutOfRangeException(nameof(changes));
            token.ThrowIfCancellationRequested();
            string tag = settings.Tags[change.Key];
            int handle = await GetHandleAsync(row, tag, token);
            int capacity = plctag.plc_tag_get_string_capacity(handle, 0);
            if (capacity < 0) Check(capacity);
            if (change.Value.Length > capacity) throw new FormatException($"{settings.Labels[change.Key]} is too long for {tag}; maximum {capacity} characters.");
        }
        foreach (var change in changes)
        {
            token.ThrowIfCancellationRequested();
            string tag = settings.Tags[change.Key];
            int handle = await GetHandleAsync(row, tag, token);
            Check(plctag.plc_tag_set_string(handle, 0, change.Value));
            await WaitAsync(handle, plctag.plc_tag_write(handle, 0), token);
            string actual = await ReadLogixStringAsync(row, tag, token);
            if (!string.Equals(actual, change.Value, StringComparison.Ordinal))
                throw new PlcReadException($"PLC did not confirm the value written to {tag}.");
        }
    }

    private async Task<string> ReadLogixStringAsync(PlcRow row, string name, CancellationToken token)
    {
        int handle = await GetHandleAsync(row, name, token);
        await WaitAsync(handle, plctag.plc_tag_read(handle, 0), token);
        var type = new byte[32];
        Check(plctag.plc_tag_get_byte_array_attribute(handle, "raw_tag_type_bytes", type, type.Length));
        int size = plctag.plc_tag_get_size(handle);
        if (size < 1 || size > 8192) throw new FormatException($"Unsupported tag size: {size} bytes.");
        var bytes = new byte[size];
        Check(plctag.plc_tag_get_raw_bytes(handle, 0, bytes, size));
        return ValueDecoder.Decode(bytes, type[0], "STRING");
    }

    private async Task<int> GetHandleAsync(PlcRow row, string name, CancellationToken token)
    {
        if (!DeveloperTrackingSettings.IsValidTag(name)) throw new FormatException($"Invalid developer-tracking tag mapping: {name}");
        string key = $"{row.Key}|tracking|{name}";
        if (handles.TryGetValue(key, out int existing)) return existing;
        string path = row.Route.Length == 0 ? "," : row.Route;
        string attributes = $"protocol=ab_eip&gateway={row.Ip}&path={path}&plc=ControlLogix&elem_count=1&name={name}&use_connected_msg=1&allow_packing=1";
        int handle = plctag.plc_tag_create(attributes, 0);
        Check(handle < 0 ? handle : 0);
        handles[key] = handle;
        try { await WaitAsync(handle, plctag.plc_tag_status(handle), token); return handle; }
        catch { Remove(key); throw; }
    }


    private static async Task WaitAsync(int handle, int status, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(2000);
        try
        {
            while (status == 1)
            {
                await Task.Delay(25, timeout.Token);
                status = plctag.plc_tag_status(handle);
            }
            Check(status);
        }
        catch (OperationCanceledException)
        {
            plctag.plc_tag_abort(handle);
            token.ThrowIfCancellationRequested();
            throw new PlcReadException("Read timed out; check the PLC route and network.");
        }
    }

    private static void Check(int status)
    {
        if (status >= 0) return;
        string reason = status switch
        {
            -19 => "Tag or route not found",
            -18 => "Read not allowed by device",
            -32 => "Read timed out",
            -35 => "Unsupported type or operation",
            -3 or -6 => "Controller connection failed",
            _ => "PLC read failed"
        };
        throw new PlcReadException($"{reason} ({plctag.plc_tag_decode_error(status)}).");
    }

    private void Remove(string key)
    { if (handles.TryRemove(key, out int handle)) plctag.plc_tag_destroy(handle); }
    public void Dispose() { foreach (var key in handles.Keys) Remove(key); }
    private sealed class PlcReadException(string message) : Exception(message);
}
