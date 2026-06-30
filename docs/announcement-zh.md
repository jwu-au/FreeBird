# 🐦 FreeBird 发布啦！

> 让你的网易云音乐「重获自由」—— 一键变成能在任何地方播放的音乐文件 🎵

# 这是什么？

你有没有过这种烦恼：在网易云听了/下了一堆好歌，文件明明在硬盘里，却是一堆打不开的 `.uc` 缓存或 `.ncm` 下载文件，换个播放器就没法听了？

**FreeBird 就是来解决这个问题的！** 它是一个**基于 .NET 10** 构建的跨平台命令行工具，**自包含打包、无需预装任何运行时 —— 下载解压即用** 🚀。它能把这些文件变成干干净净的 **MP3 / FLAC / M4A**，自动命名成 `歌手 - 歌名.flac`，还会帮你：

- ✅ 自动写好标题、歌手、专辑等标签
- ✅ 校验音频完整性，确保转出来的文件没坏
- ✅ **绝不修改你的原始文件**，只读不写，安全放心
- 📦 **绿色免安装**，不用装 .NET、不用配环境，下载就能跑
- 🖥️ 全平台支持：**macOS、Linux、Windows** 都能用

## 支持两种网易云文件

| 类型 | 来源 | 说明 |
|---|---|---|
| **`.uc` / `.uc!` 缓存文件** | 在线**试听**时生成 | FreeBird 自动从网易云抓取歌曲信息来命名和打标签 |
| **`.ncm` 下载文件** | **下载**歌曲时生成 | 歌名、歌手、专辑、封面都已内嵌在文件里，**完全离线解码、无需联网**，并自动写入专辑封面 🖼️ |

# 怎么用？超简单！

下载对应系统的版本，解压，然后一条命令搞定：

```bash
# 转换一次就好：
fb scan <你的文件夹> --output ~/Music/decoded

# 或者让它一直运行，边听/边下边自动转换新文件：
fb watch <你的文件夹> --output ~/Music/decoded
```

`<你的文件夹>` 可以是网易云的**在线缓存目录**（`.uc` / `.uc!`），也可以是你存放**下载歌曲**（`.ncm`）的任意目录 —— 两种文件 FreeBird 都会自动识别处理。

**还能一次传入多个文件夹**，比如把「在线缓存」和「下载目录」一起转到同一个输出文件夹：

```bash
# 在 --output 前面想写几个输入文件夹都行，全部转到同一个输出目录：
fb scan ~/Music/netease-cache ~/Downloads/netease --output ~/Music/decoded

# fb watch 同样支持多个文件夹，会同时盯着它们：
fb watch ~/Music/netease-cache ~/Downloads/netease --output ~/Music/decoded
```

转换好的音乐就在 `~/Music/decoded` 里，用你喜欢的播放器打开就行，**就这么简单！** 🎉

> 💡 **在线缓存文件夹在哪？**
> - **macOS**：`~/Library/Containers/com.netease.163music/Data/Caches/online_play_cache`
> - **Windows**：`C:\Users\<你>\AppData\Local\Netease\CloudMusic\Cache\Cache`

## 🪟 想让它开机自动运行？（Windows）

把 FreeBird 装成 Windows 后台服务，开机自启、有新文件自动转换，完全不用管。服务模式通过一个 JSON 配置文件来设置输入/输出目录：

```powershell
# 以管理员身份打开 PowerShell：

# 1. 生成默认配置文件（默认写到 %ProgramData%\FreeBird\config.json）
fb service init

# 2. 用记事本打开生成的 config.json，填好你的输入文件夹和输出文件夹后保存

# 3. 安装并启动服务（不带 --config 时默认读上面那个路径）
fb service install
fb service start

# 查看运行状态 / 停止：
fb service status
fb service stop
```

## ⚙️ 几个常用选项

```bash
# 指定完整性校验级别（auto 会在 Windows 上自动准备 flac 工具）：
fb scan <你的文件夹> --output ~/Music/decoded --integrity auto

# 不想写入元数据标签（同时也会跳过 .ncm 的专辑封面）：
fb scan <你的文件夹> --output ~/Music/decoded --no-write-tags
```

> 💡 想看全部命令和选项，运行 `fb --help`、`fb scan --help` 或 `fb watch --help`。

# ✨ 亮点功能

- 🎵 **同时支持试听缓存与下载文件**：`.uc` / `.uc!` 在线缓存和 `.ncm` 下载文件都能转，按扩展名自动识别
- 🖼️ **`.ncm` 完全离线 + 自动封面**：下载文件自带歌名/歌手/专辑/封面，无需联网即可解码并写入专辑封面
- 🪟 **Windows 后台服务模式**：开机自动运行，有新文件自动转换，完全不用管
- 🔄 **更聪明的元数据获取**：`.uc` 在网络不好或被限流时会自动重试，等网络恢复就补上正确的歌名
- 🔧 **Windows 上 FLAC 校验开箱即用**：需要时自动下载 flac 工具，无需手动安装

# 📦 开源地址

完全开源，欢迎 Star / Issue / PR！

- **项目主页**：https://github.com/jwu-au/FreeBird
- **下载地址**：https://github.com/jwu-au/FreeBird/releases
- **技术栈**：.NET 10 · C#（自包含发布，无需安装运行时，开箱即用）
- **开源协议**：MIT License

# 💡 缘起

- 云音乐的播放器不支持源码输出到我的音频解码器 DAC，我的初衷就是用 Foobar2000 直接播放 FLAC 然后无损输出到解码器达到 HIFI 的目的。又可以通过 USB 拷贝音频文件到车上播放，从而脱离云播放器。希望你喜欢。:)

---

让你的音乐自由飞翔吧 🕊️
