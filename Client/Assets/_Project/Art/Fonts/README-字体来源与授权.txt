Alibaba PuHuiTi 3.0（阿里巴巴普惠体 3.0）—— 55 Regular

【两个文件，各是什么】
  AlibabaPuHuiTi-3-55-Regular.ttf          8.5 MB   源字体（TMP 动态模式要它进包）
  AlibabaPuHuiTi-3-55-Regular SDF.asset    2.1 MB   TMP 字体资产（Dynamic / 1024² / 含 71 个已烘焙字形）
  ⚠️ 两份是**一起从负责人另一个工程 RPG2 搬过来的**（Assets/Fonts/AlibabaPuHuiTi-3-55-Regular/），
     连 .meta 一起搬 -> GUID 不变 -> 资产里的"源字体引用"不会断。

【为什么是 .ttf 而不是 .otf（2026-09-23 实测踩了两次，值得记）】
  ① 最初用 .otf（14.9 MB）：字体资产建出来了、TMP 默认字体与兜底也都接上了，
     但 TMP 一个字形都没加进去（资产里 m_UsedGlyphRects 一直空）-> 屏幕上是方框。
     TMP 的动态字形加载走 FreeType，对 OTF（CFF 轮廓）支持不佳。
  ② 换成 .ttf 后用 Tools/NBC/UI/① 生成：图集是空的
     （Texture2D.m_CompleteImageSize: 0、宽高 0）-> 屏幕上一个字都不显示。
     所以**不再用脚本生成**，改用 RPG2 那份已验证可用的资产。
  （eot / woff / woff2 是网页格式，Unity 用不到，一律不放。）

【许可】阿里巴巴普惠体对外免费商用（以阿里巴巴官方字体页面授权说明为准）。

【用途】本项目中文显示字体（TextMeshPro）。接线方式见 Docs\24-中文字体接入.md。
【注意】只放了一份 Regular；要加粗/其它字重请另拷文件，不要在本文件上手改。
        包体优化（按用到的字做子集化）排在 M5。