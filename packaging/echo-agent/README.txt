JST Echo Reset Agent - portable package

This package is configured for the Logix Echo server at 10.10.10.200.
Administrator rights are not required.

FIRST TEST
1. Double-click "Validate Echo Inventory.cmd".
2. Confirm the inventory includes the expected V34 and V36 controllers.

RUN THE AGENT
1. Double-click "Start Echo Reset Agent.cmd".
2. Keep the console window open.
3. In PLC Finder, use the default agent address and click Connect Echo agent.

Portable mode uses the trusted internal network and does not require a pairing key
or certificate thumbprint.

You may disconnect the Remote Desktop session. Signing out, closing the console,
or pressing Ctrl+C stops the portable agent.

The pairing key and certificate are stored under the current Windows user's
profile and are reused on later launches.
