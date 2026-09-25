# JST Echo Reset Agent setup

The agent must run on the Windows server that hosts FactoryTalk Logix Echo. PLC Finder continues to run on the engineering workstation.

## Prerequisites

- Windows x64 and FactoryTalk Logix Echo installed on the server.
- The FactoryTalk Logix Echo Service must be enabled and running.
- An administrator account for the one-time agent installation.
- TCP 47443 reachable from the engineering workstation. The installer opens this port for Domain and Private Windows Firewall profiles.

The agent loads Rockwell's API assemblies from the server's existing Logix Echo installation. Rockwell files are not redistributed with the agent.

## Run without administrator rights

If you can sign in through Remote Desktop but cannot elevate, copy `JST Echo Reset Agent.exe` to the server and run this from a normal PowerShell window:

```powershell
& '.\JST Echo Reset Agent.exe' --portable --host 10.10.10.200
```

Portable mode stores its TLS certificate under the current Windows user's profile and listens with an application-owned HTTPS server, so it does not require service installation, HKLM access, an HTTP.sys certificate binding, or a pairing key. PLC Finder connects automatically to the configured agent address. Keep the console window open. You may disconnect the RDP session, but signing out or closing the console stops the agent.

Windows Firewall policy still applies. If TCP 47443 remains unreachable from the engineering workstation, an administrator must permit inbound TCP 47443 (preferably limited to approved engineering workstation addresses), or organizational policy must provide an already-approved port. The program cannot bypass that policy without elevation.

To verify that the signed-in Windows account can read Echo before starting the listener, run:

```powershell
& '.\JST Echo Reset Agent.exe' --validate-sdk --portable --host 10.10.10.200
```

## Install on 10.10.10.200

1. Copy only `JST Echo Reset Agent.exe` to the server.
2. Open PowerShell **as Administrator** in the folder containing the executable.
3. Run:

```powershell
& '.\JST Echo Reset Agent.exe' --install --host 10.10.10.200
```

The installer copies the executable to `C:\Program Files\JST\Echo Reset Agent`, creates an automatic Windows service, creates and binds a five-year server certificate, generates a 256-bit pairing key, and adds the firewall rule. It prints three values:

- Agent address
- Pairing key
- Certificate thumbprint

Save these values when they are displayed. The pairing key is not printed again.

## Connect PLC Finder

In PLC Finder, expand **Advanced connection options**, enter the three values under **Logix Echo reset agent**, and select **Connect Echo agent**. A successful connection reports the number of controllers in the live Echo inventory.

After connecting, uniquely addressed controllers from the signed Echo inventory appear even when they do not answer EtherNet/IP; these rows show **Echo not responding** and remain eligible for reset. A responding row receives an **Echo** badge only when the live inventory proves a unique match using controller serial number and IP, plus chassis and slot for a routed controller. Duplicate or ambiguous IP records are never offered as reset targets. The reset request contains an Echo controller GUID and is executed by the Rockwell API on the server; no reset command is sent over EtherNet/IP to the selected address.

## Validate and troubleshoot

Run this on the server to read the inventory without changing a controller:

```powershell
& 'C:\Program Files\JST\Echo Reset Agent\JST Echo Reset Agent.exe' --validate-sdk
```

If the FactoryTalk security policy blocks the Local System service account, configure the `JstEchoResetAgent` Windows service to run under an approved service account and restart it. The account must be able to use the local Echo Service API.

If the server network is classified as **Public**, change the generated `JST Echo Reset Agent` firewall rule to include that profile or correct the Windows network profile. Limit the rule to approved engineering workstation addresses where practical.

To troubleshoot interactively, stop the Windows service and run the agent from an elevated terminal:

```powershell
& 'C:\Program Files\JST\Echo Reset Agent\JST Echo Reset Agent.exe' --console
```

## Remove the service

Run from an elevated PowerShell terminal:

```powershell
& 'C:\Program Files\JST\Echo Reset Agent\JST Echo Reset Agent.exe' --uninstall
```

This removes the Windows service, agent firewall rule, HTTPS binding, certificate, and registry configuration. It does not modify FactoryTalk Logix Echo or any emulated controller.
