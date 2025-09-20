# HyperX Cloud III Wireless Tray Utility

A small Windows tray **mini-utility** that shows the battery level of **HyperX Cloud III Wireless** headset.  
Built with WinForms and depends on the [HidSharp](https://www.zer7.com/software/hidsharp) library.


## Features
- Displays battery percentage in the system tray
- Lightweight and minimal
- Supports Windows autostart (via registry)


## Screenshot
![Tray Icon](./docs/image.png)


## Device Information
| Parameter | Value |
|-----------|-------|
| **VID**   | `0x03F0` (HP Inc.) |
| **PID**   | `0x05B7` (HyperX Cloud III Wireless dongle) |


## HID Reports (RID = 0x66)
| Purpose                    | First byte | Offset of value | Notes                                    |
|-----------------------------|------------|-----------------|------------------------------------------|
| Battery percentage          | `0x89`     | 4               | Byte 4 = battery level (0–100)           |
| Connection / Disconnection  | `0x0D`     | 1               | Byte 1 = connected / disconnected flag   |
| Cable plugged-in status     | `0x8A`     | 1               | Byte 1 = cable plugged-in flag           |
| Battery level changed event | `0x0C`     | 1               | Byte 1 = event flag (battery updated)    |
| Connection status request   | `0x82`     | 1               | Byte 1 = request flag (connection check) |



## Requirements
- [.NET 8.0 (Windows)](https://dotnet.microsoft.com/)
- [HidSharp](https://www.zer7.com/software/hidsharp)
