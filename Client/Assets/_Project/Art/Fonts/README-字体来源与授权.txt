Alibaba PuHuiTi 3.0（阿里巴巴普惠体 3.0）—— 55 Regular / L3
来源：负责人提供的字体包 E:\U3D Projects\0_MyFile\AlibabaPuHuiTi-3-55-RegularL3\

文件：AlibabaPuHuiTi-3-55-RegularL3.ttf（21.7 MB）
⚠️ 为什么是 .ttf 而不是 .otf（2026-09-23 实测）：
   第一版用的是 .otf（14.9 MB）。字体资产建出来了、TMP 默认字体与兜底也都接上了，
   但 TMP **一个字形都没加进去**（资产里 m_UsedGlyphRects 一直是空的），屏幕上就是方框。
   TMP 的动态字形加载走 FreeType，**对 OTF（CFF 轮廓）支持不佳**。
   旁证：负责人另一个工程 RPG2 里那份**能用**的同名字体资产，源字体正是 .ttf。
   （eot / woff / woff2 是网页格式，Unity 用不到，一律不放。）

许可：阿里巴巴普惠体对外**免费商用**（以阿里巴巴官方字体页面的授权说明为准）。
用途：本项目的中文显示字体 —— 经 TextMeshPro 转成 SDF 字体资产使用（见 Docs\24-中文字体接入.md）。
注意：本项目只放了一份 Regular；要加粗/其它字重请另拷文件，不要在本文件上做手工子集化。
     包体优化（按用到的字子集化）排在 M5。