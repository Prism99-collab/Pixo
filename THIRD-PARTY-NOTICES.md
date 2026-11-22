# Third-party notices

Dynamic Notch includes the following open-source components. Each is used under its own license, reproduced below.

## .NET runtime and Windows Desktop (WPF, Windows Forms)

Dynamic Notch ships with its own copy of the .NET runtime so it runs without installing anything else.

- Project: https://github.com/dotnet/runtime, https://github.com/dotnet/wpf, https://github.com/dotnet/winforms
- License: MIT
- Copyright (c) .NET Foundation and Contributors

## System.Management

- Project: https://github.com/dotnet/runtime
- License: MIT
- Copyright (c) .NET Foundation and Contributors

## NAudio (NAudio.Wasapi, NAudio.Core)

- Project: https://github.com/naudio/NAudio
- License: MIT
- Copyright (c) 2020 Mark Heath

---

## MIT License

The components above are each licensed under the MIT License:

> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
> documentation files (the "Software"), to deal in the Software without restriction, including without limitation
> the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to
> permit persons to whom the Software is furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or substantial portions of
> the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
> THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
> AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
> TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
> SOFTWARE.

## BatteryHub and HeadsetControl (wireless device batteries)

Pixo includes BatteryHub 1.1.0 by krisgfx9 and HeadsetControl by Sapd, unmodified, in `Tools/BatteryHub`. They run as
separate programs: Pixo starts `BatteryHub.exe --json` and reads the list of devices it prints. Both are licensed under
the GNU General Public License v3 (the full text is in `Tools/BatteryHub/LICENSE.txt`).

- BatteryHub source: https://github.com/krisgfx9/Wireless-Device-Battery-Monitor (tag v1.1.0)
- HeadsetControl source: https://github.com/Sapd/HeadsetControl (4.3.0, also attached to BatteryHub's v1.1.0 release)

## Fonts and icons

Dynamic Notch uses fonts that come with Windows (Segoe UI, Segoe Fluent Icons / Segoe MDL2 Assets, Bahnschrift,
Consolas). They aren't included in the app; Windows provides them.

## Installer

The installer is built with Inno Setup (https://jrsoftware.org/isinfo.php), used under its license.
