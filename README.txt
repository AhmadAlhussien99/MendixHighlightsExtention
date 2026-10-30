# Document Highlighter for Mendix Studio Pro

Color-code items in the Studio Pro **App Explorer**. Right-click any page, microflow, folder, module or other item and
choose **Highlight → Green / Orange / Red**. On a module, the menu also shows how many items of each color it contains.

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