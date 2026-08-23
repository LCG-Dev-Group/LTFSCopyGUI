# LTFSCopyGUI

![LTFSCopyGUIIcon](./docs/logo.png)

## LTFS文件排序复制工具

[![Ask DeepWiki](https://deepwiki.com/badge.svg)](https://deepwiki.com/zhaoyangwx/LTFSCopyGUI)

### 主要功能

#### LTFSCopyGUI.exe：索引排序生成复制脚本

排序生成脚本功能适用HP LTFS

根据离线索引schema文件，对文件存放的block进行排序，并产生命令行用来复制文件。若Partition A有文件，先复制Partition A的文件。

#### LTFSConfigurator.exe：磁带挂载管理/直接读写

盘符挂载适用HPLTFS

直接读写适用HP/IBM或者第三方OEM驱动器  

**${\color{red}{\textrm{使用直接读写功能时，请勿挂载盘符}}}$**

如果OEM驱动器没有安装驱动，可以使用设备路径例如\\\\.\GLOBALROOT\Device\00000043

#### LTFSCopyGUI.exe CLI

LTFSCopyGUI.exe /?查看命令行用法

##### 目前支持功能
    LTFSCopyGUI.exe -t
    LTFSCopyGUI.exe -rb
    LTFSCopyGUI.exe -wb
    LTFSCopyGUI.exe -raw
    LTFSCopyGUI.exe -mkltfs

### 版本更新说明

v1.x 排序生成脚本、复制&校验功能

v2.x 驱动器控制、SCSI指令面板、磁带标签修改、磁带信息读取

LtfsCommand from **[inaxeon/ltfscmd](https://github.com/inaxeon/ltfscmd)**

v3.x LTFS直接读写

### 关于加密

已集成至LTFSWriter，通过“设置密钥”或者“设置密码”向驱动器发送加密参数。

对于开启加密的磁带，请不要启用自动重装带（重装带前清洁次数改成0禁用），否则重新装带会重置驱动器加密密钥导致写入失败。

### How to switch language:
    lang.ini to set language (Currently en for English, zh for Chinese Simplified. zh Default)
    if no lang.ini exist, will follow system language setting

---

演示视频（bilibili）：**[BV1j24y177PF](https://www.bilibili.com/video/BV1j24y177PF)**  **[BV1Gy4y1f7WP](https://www.bilibili.com/video/BV1Gy4y1f7WP)**

---


**欢迎加入LTO磁带技术交流QQ群 433387693 获取开发中的最新版本，以及相关资料**

软著登字第11348107号
