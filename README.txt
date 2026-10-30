# Document Highlighter for Mendix Studio Pro

By Ahmad Alhussien - https://www.linkedin.com/in/ahmadalhussien/

Color-code items in the Studio Pro **App Explorer**. Right-click any page, microflow, folder, module or other item and
choose **Green / Orange / Red** under **Highlight**. On a module or folder, the menu also shows how many items of each
color it contains and can remove them all, and a module or folder that isn't highlighted itself but has highlighted
items inside shows a small flag after its name.

Shortcuts, while the App Explorer has focus: Ctrl+Alt+1 Green, Ctrl+Alt+2 Orange, Ctrl+Alt+3 Red, Ctrl+Alt+0 Remove.

Highlights are stored on your own machine, outside the app/project, so nothing is ever committed. Works with Studio Pro
10.24.1 and newer, including 11.x.

--------------------------------------------------------

## Install

Close Studio Pro, then right-click the folder and select Open in Terminal (PowerShell). From that folder, run the following command and choose the Studio Pro version you want to install into:

powershell -ExecutionPolicy Bypass -File .\install.ps1

--------------------------------------------------------

## Uninstall

Run this in PowerShell the same way:

powershell -ExecutionPolicy Bypass -File .\uninstall.ps1