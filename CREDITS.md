# Credits

## Vibe Salamander

- **Author:** Claude (Anthropic's AI model) wrote the code and the design.
- **Guide:** **BrainCluster** came up with Vibe Salamander and guided it: the idea, the direction,
  the requirements, the testing and every decision about what it should be.

"Vibe Salamander Authors" in the copyright notices means both of them (see [AUTHORS](AUTHORS)).

## Open Salamander

Vibe Salamander is a new program written from scratch in C# and WinUI 3, but it exists because of
**Servant Salamander / Altap Salamander / Open Salamander**: its layout, its keyboard model and most of
its features follow that program, and we are deeply grateful to everyone who built it.

- The original **Servant Salamander** was written by **Petr Šolín** while studying at the Czech
  Technical University and released as freeware in 1997.
- Petr Šolín and **Jan Ryšavý** founded **[Altap](https://www.altap.cz/)**, which developed
  **Altap Salamander** for more than two decades.
- In 2019 Altap was acquired by **[Fine](https://www.finesoftware.eu/)**, which released Altap
  Salamander 4.0 as freeware and in 2023 published the source code as
  **[Open Salamander](https://github.com/OpenSalamander/salamander)** under GPL-2.0-or-later.

### Open Salamander authors

(From the Open Salamander [AUTHORS](https://github.com/OpenSalamander/salamander/blob/main/AUTHORS) file.)

#### Open Salamander Core and Plugins

- David Andrš
- Lukáš Cerman
- Jakub Červený
- Tomáš Jelínek
- Milan Kaše
- Tomáš Kopal
- Jan Patera
- Martin Přikryl
- Juraj Rojko
- Jan Ryšavý
- Petr Šolín

#### Translators

- **Czech**
  - Hynek Mikulič
- **Dutch**
  - Marco Fung
  - S.V. (Stephan) Rietveld
  - Cor van der Smissen
  - Patrick Vijgeboom
- **French**
  - Maxime Abbey
  - Arnaud Dupin de Beyssat
  - Gérard Gréco
  - Jérémy Marc
- **German**
  - Dirk Bornhütter
  - Marko Meister
  - Thomas Kelso
  - Thomas Pagels
  - Mick Weiss
  - Thomas Wölk
- **Hungarian**
  - Panel Beállítások
- **Chinese simplified**
  - Michael Lu
- **Romanian**
  - Donca Radu
  - Ioan Stefancu
- **Russian**
  - Neutron16
  - Vasiliy
  - Игорь Полякофф
  - Ален Хайруллин
- **Slovak**
  - Roman Paholík
- **Spanish**
  - Juan Pablo Barrio Lera

## Application icon

`src/VibeSalamander.App/Assets/VibeSalamander.ico` and `VibeSalamander.png` are based on the Open
Salamander application icon (`src/res/salamand.ico`),
© 2023 Open Salamander Authors, licensed under GPL-2.0-or-later and used here under GPL-3.0-or-later.

**Modifications (2026):** the orange disc was recolored to light purple; the shapes are unchanged.

## Third-party components

| Component | License | Use |
|---|---|---|
| [.NET](https://github.com/dotnet/runtime) | MIT | runtime |
| [Windows App SDK / WinUI 3](https://github.com/microsoft/WindowsAppSDK) | MIT | user interface |
| [SharpCompress](https://github.com/adamhathcock/sharpcompress) | MIT | archive plugin (zip, 7z, rar, tar, gz, bz2, xz, lz) |
| [FluentFTP](https://github.com/robinrodricks/FluentFTP) | MIT | FTP / SFTP plugin (FTP, FTPS) |
| [SSH.NET](https://github.com/sshnet/SSH.NET) | MIT | FTP / SFTP plugin (SFTP) |
| [Monaco Editor](https://github.com/microsoft/monaco-editor) | MIT | the built-in editor (F4); downloaded by the build, its LICENSE is in Assets\Monaco |
| [Markdig](https://github.com/xoofx/markdig) | BSD 2-clause | Markdown in the viewer and the editor's preview |
| [YamlDotNet](https://github.com/aaubry/YamlDotNet) | MIT | YAML trees and checks |
| [Microsoft Edge WebView2](https://learn.microsoft.com/microsoft-edge/webview2/) | Microsoft (part of Windows) | the previews and the editor |
| [7-Zip](https://www.7-zip.org/) by Igor Pavlov | GNU LGPL (+ BSD 3-clause parts, unRAR restriction) | the separate 7-Zip plugin download: its 7z.exe, 7z.dll and 7z.sfx are the unchanged 7-Zip, with 7-Zip's License.txt; the source is at 7-zip.org |

## License

Vibe Salamander is free software: you can redistribute it and/or modify it under the terms of the
GNU General Public License as published by the Free Software Foundation, either version 3 of the
License, or (at your option) any later version. See [LICENSE](LICENSE).

This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even
the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
