EchoGarage 转换器附带的外部工具
================================

本目录中的命令行工具用于“转换”功能（AAC / FLAC / MP3 / WAV）。发布脚本会把整个
tools 目录复制到发布包的 tools\ 下；程序按以下顺序查找工具：

1. 转换器设置 → 编码器与工具中显式指定的路径；
2. 程序目录 tools\ 下的同名 exe（即本目录）；
3. 程序目录下的同名 exe；
4. 系统 PATH。

Nero AAC 不随包附带（Nero EULA 禁止再分发），需要用户自行下载并在设置中指定
neroAacEnc.exe 的路径。

----------------------------------------------------------------
flac.exe 1.4.3
  用途：FLAC 编码。
  来源：https://xiph.org/flac/download.html（Windows 版）
  许可：FLAC 命令行工具以 GPL 发布；libFLAC 为 BSD 类许可。
  许可全文：flac_license.txt
  源码获取：https://xiph.org/flac/download.html

lame.exe 3.100.1
  用途：MP3 编码。
  来源：https://lame.sourceforge.io/
  许可：GNU LGPL。
  许可全文：lame_license.txt
  源码获取：https://lame.sourceforge.io/

ssrc.exe 2.4.0（Shibatch Sample Rate Converter）
  用途：高质量重采样（SSRC，foobar2000 内置 dBpoweramp/SSRC 重采样器的开源同源方案）。
  来源：https://github.com/shibatch/SSRC/releases/tag/v2.4.0
  许可：Boost Software License 1.0（宽松许可）。
  许可全文：LICENSE.txt（Boost Software License 1.0）
  其他说明：NOTICE.txt；ssrc-licenses\LICENSE-dr_libs.txt（依赖的 dr_libs 许可）
  源码获取：https://github.com/shibatch/SSRC

----------------------------------------------------------------
所有工具均为原样分发，未做修改。若替换版本，请同步更新本文件中的版本号与许可说明。
