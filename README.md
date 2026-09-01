# LTFSCopyGUI

![LTFSCopyGUIIcon](./docs/logo.png)

> [!WARNING]
> LTFSCopyGUI is a **source-available** software. If you wish to make any changes, please first consult the contributors through any available platform/IM.
>
> LTFSCopyGUI **is not designed for enterprise-level stability**, and many of its other features are **highly risky**. If you do not have the ability to troubleshoot and recover from failures on your own, please consider using commercial backup software.

[![查看 - 中文文档](https://img.shields.io/badge/查看-中文文档-green?style=for-the-badge)](./README.zh-CN.md "Go to project documentation")

[![OS - Windows](https://img.shields.io/badge/OS-Windows-blue?logo=windows&logoColor=white)](https://www.microsoft.com/ "Go to Microsoft homepage")
[![Build installer](https://github.com/LCG-Dev-Group/LTFSCopyGUI/actions/workflows/ci.yml/badge.svg)](https://github.com/LCG-Dev-Group/LTFSCopyGUI/actions/workflows/ci.yml)
![Made with VB.NET & C++ & Rust](https://img.shields.io/badge/Made_with-VB.NET%20%26%20C++%20%26%20Rust-blue?logo=visual-basic&logoColor=white)
[![Ask DeepWiki](https://deepwiki.com/badge.svg)](https://deepwiki.com/zhaoyangwx/LTFSCopyGUI)

## Features

- Merge/sort LTFS index
- Read/write LTFS directly without mounting it as a drive (even no driver installed)
- Hash when writing files
- Enrich `tar` metadata
- DEBUG functionality
- some cli commands(use /?)

### For encryped tape

This feature has been integrated into LTFSWriter; use “Set Key” or “Set Password” to send encryption parameters to the drive.
For tapes with encryption enabled, do not enable automatic tape reloading (disable it by setting the “Clean before reloading” count to 0); otherwise, reloading the tape will reset the drive’s encryption key, causing the write operation to fail.

### How to switch language

`config/lang.ini` to set language (Currently en for English, zh for Chinese Simplified. zh Default).
If no `config/lang.ini` exist, will follow system language setting.

---

演示视频（bilibili）：**[BV1j24y177PF](https://www.bilibili.com/video/BV1j24y177PF)**  **[BV1Gy4y1f7WP](https://www.bilibili.com/video/BV1Gy4y1f7WP)**

软著登字第11348107号
