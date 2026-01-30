# Shader Explorer for Stride
A tool showing the [built-in shaders](https://doc.stride3d.net/latest/en/manual/graphics/effects-and-shaders/shading-language/index.html) of the [Stride game engine](https://stride3d.net/) and [vvvv gamma](https://vvvv.org/) and their inheritance hierarchy.

## VS Code Extension

<a href="https://marketplace.visualstudio.com/items?itemName=tebjan.sdsl">
  <img src="VSCode/vscode-extension/icons/icon.png" alt="Stride Shader Tools" width="128px"/>
</a>

**[Stride Shader Tools](https://marketplace.visualstudio.com/items?itemName=tebjan.sdsl)** - VS Code extension for SDSL shader development with:

* Syntax highlighting for `.sdsl` files
* IntelliSense with completions for shaders, variables, and methods
* Hover info showing inherited members and type information
* Go-to-definition for base shaders
* Diagnostics for undefined identifiers with quick-fix suggestions
* Inheritance tree view panel
* Template shader support

Install from the [VS Code Marketplace](https://marketplace.visualstudio.com/items?itemName=tebjan.sdsl).

---

## Desktop Application

Parses all shaders in user\\.nuget\packages\stride.packagename\latest-version

Features:
* Lists base shaders
* Lists derived shaders
* Navigation history
* Shows in which shader a method or variable is defined or used
* Search for a shader and/or its variables/methods
* Allows to add custom shader folders
* Show selected shader in file explorer

<img src="Stride.ShaderExplorer/Assets/Screenshot.png" alt="Screenshot" width="700px"/>

Download [here](https://github.com/tebjan/Stride.ShaderExplorer/releases/).

## Usage
* Make sure [Stride](https://www.stride3d.net) is installed
* Extract and run the exe
* Pin it to the taskbar if you use it frequently

## Build & Run
Build with Visual Studio 2019 or newer.
