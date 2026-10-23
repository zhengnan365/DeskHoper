---
layout: home

hero:
  name: DeskHoper
  text: 一套显示器、键盘和鼠标，两台电脑轮流用
  tagline: 外接屏切给谁看、文件怎么过去、键鼠归谁敲，收在一个窗口里。不用装服务端，不用账号，两台机器各跑一个 exe，同一个局域网里直接用。
  image:
    src: /favicon.png
    alt: DeskHoper 图标
  actions:
    - theme: brand
      text: 下载 Windows 版
      link: https://github.com/zhengnan365/DeskHoper/releases/latest
    - theme: alt
      text: 先看能干什么
      link: /features
    - theme: alt
      text: 怎么连上对面
      link: /start

features:
  - title: 屏幕切换
    details: 通过 DDC/CI 直接改显示器的输入源，不动 Windows 的分辨率和排列。两套接线各存一张卡片，点一下按每台显示器分别切过去。
  - title: 投影模式
    details: 仅电脑屏幕、复制、扩展、仅第二屏幕四个状态一个按钮，跟场景一起记下来，切场景时一起改。
  - title: 剪贴板同步
    details: 复制即同步，不用点任何东西。文本双向走，对面直接粘进微信、Word 或资源管理器。
  - title: 文件拖放传输
    details: 把文件拖进拖放区才发送，本机复制文件不会误发。收发都有进度，同名不覆盖，收下的同时写进本机剪贴板。
  - title: 键鼠共享
    details: 一套键盘鼠标控制两台机器，换卡片时对面自动镜像成互补角色。键盘和鼠标可以分开共享，Ctrl + Alt + Q 随时交回。
  - title: 只走局域网
    details: 没有云端中转、没有账号。TCP + TLS 直连，自签证书并 pin SHA-256 指纹，数据不出你家网络。
---

## 三页界面

顶部三个菜单，三页共用同一个固定尺寸的窗口，切页时高度不会跳。

![屏幕切换页](/images/screen-switch.png)

| 页面 | 管什么 | 需要两台机器都开着吗 |
| --- | --- | --- |
| 屏幕切换 | 显示器输入源、投影模式、逐台控制 | 不需要，只管本机接的屏 |
| 文件共享 | 剪贴板文本同步、文件拖放收发 | 需要，两台要配对 |
| 键鼠共享 | 一套键盘鼠标控制两台机器 | 需要，两台要配对 |

## 上手三步

1. 两台机器各下载解压一份，双击 `DeskHoper.exe`。
2. 都停在**文件共享**页，一台生成 6 位配对码，另一台点列表里的它、输码。
3. 连上之后，屏幕切换点卡片，文件拖进拖放区，键鼠换卡片。

详细步骤和端口、防火墙设置见[快速开始](/start)与[网络与安全](/network)。

## 运行要求

- Windows 10 / 11，x64
- 支持 DDC/CI 的外接显示器，并且显示器 OSD 菜单里已开启 DDC/CI
- 文件共享和键鼠共享需要两台机器在同一个局域网内

self-contained 发布包，目标机不需要单独安装 .NET Runtime。
