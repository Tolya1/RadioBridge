# libs

Optional, for local Windows builds only. Put a **64-bit** `SimConnect.dll` here and the build copies
it next to the app, which is the first place RadioBridge looks for it. The file is Microsoft's and is
ignored by git: never commit it.

Copies ship with the MSFS SDK (`<SDK>\SimConnect SDK\lib\SimConnect.dll`). See the README for the
other places the app searches.
