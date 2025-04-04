# Rambo (RAM Booster & Optimizer)

![RAM Usage](https://raw.githubusercontent.com/alfaoz/rambo/refs/heads/master/rambouiex.png)


**Rambo** (short for: **RAM Booster & Optimizer**, **RAM-Turbo**, or just plain **Rambo**) is a simple utility for Windows designed to monitor and attempt to optimize system memory (RAM) usage.

## Features

* **Real-time Memory Monitoring:** Displays total, used, available, and percentage of RAM currently in use.
* **Top Processes:** Shows a list of the top 5 memory-consuming processes (excluding the system idle process and Rambo itself).
* **Dynamic Tray Icon:** The system tray icon dynamically updates to show the current RAM usage percentage as a vertical bar graph and in the tooltip text.

## Requirements

* **Operating System:** Windows 10 / 11
* **Permissions:** Administrator privileges are recommended for the `EmptyWorkingSet` function to work effectively on more processes.

## Usage

1.  **Run:** Launch `rambo.exe`.
2.  **Monitor:** Observe the current RAM usage statistics and the visual bar.
3.  **Optimize:** Click the `>_optimize memory` button to attempt optimization. The status bar will indicate progress and results.
4.  **Run on Boot:** Check or uncheck the "Run on Boot?" checkbox to control automatic startup behavior. Changes are saved to the Windows Registry.
5.  **Minimize:**
    * Use the window's minimize button (`_`) to send the application to the system tray.
    * Use the taskbar icon to minimize normally.
6.  **Restore:** Click the Rambo icon in the system tray or use the "Show Rambo" option from its context menu to restore the main window.
7.  **Exit:** Use the window's close button (`X`) or the "Exit" option in the tray icon's context menu. This completely exits the app.

## Building from Source (Optional)

Rambo is a Windows Presentation Foundation (WPF) application written in C#. To build it, you will need:

* Visual Studio (e.g., 2022 or later) with the ".NET desktop development" workload installed.
* The corresponding .NET SDK. (10.0+)

Open the solution file (`.sln`) in Visual Studio and build the project.

## Disclaimer

RAM optimizers work by requesting the operating system to trim the memory working set of running processes. While this can sometimes free up physical RAM temporarily, the OS memory manager is generally efficient. The actual performance benefits of such tools can vary significantly depending on the system, running applications, and usage patterns. Overly aggressive optimization could potentially lead to decreased performance if applications need to reload data back into RAM frequently. Use this tool responsibly.

## License

This project is licensed under the MIT License.

```text
MIT License

Copyright (c) 2025 Alfa Ozaltin

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
