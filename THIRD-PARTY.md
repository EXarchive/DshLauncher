# 第三方资源署名

本项目包含下列第三方作品。

---

## 应用图标（`src/DshLauncher.ico`）—— 素材来源为用户提供的二创图

图标由**项目作者提供的角色插画**（蓝发、鲸耳鲸尾、女仆装）生成：

1. 用边界洪水填充抠掉外部纯白背景；
2. 按尺寸裁剪（大尺寸给足头部与肩，小尺寸收紧到脸部，保证 16px 仍可辨识）；
3. 合成到深蓝渐变圆角方块上（`#24406B → #101B33`）；
4. 逐尺寸降采样，产出 9 个尺寸的 ICO。

### 权利状态说明（如实记录，不做无依据的声明）

该素材为**二次创作（二创）**作品，非本项目原创。需要明确的是：

- 「二创」**不等于**「无版权」：二创作品本身通常仍由其二创作者享有著作权；
  若原作受保护，未经许可的二创还可能涉及对原作的侵权。
- 因此本项目的表述是：**素材权利归属未作核实**，其使用与再分发的风险由本项目作者承担。

若后续需要一张权利链条完全清晰的图标，`design/candidates/lucide_terminal.svg` 保留了那张开源图标的源文件（MIT/ISC 可商用），需要时可用 `design/` 下的脚本重新生成一套并替换。

生成脚本与中间产物保留在 `design/`（`mascot-src.png`、`build-mascot-icon.mjs`），
更换素材即可重新产出全部尺寸。

---

## 备选图标（开源，可商用）

若需要一张**权利清晰**的图标，`design/` 下已备好一套：以 **Lucide** 的 `terminal`
图标改作，蓝底白 `❯_`，同样含 9 个尺寸。

- 图标集：Lucide — https://github.com/lucide-icons/lucide
- 该图标适用 **MIT**（非 Lucide 整体的 ISC）：Lucide 的 LICENSE 明确列出
  「派生自 Feather」的图标名单，`terminal` 在名单内 → `Copyright (c) 2013-present Cole Bemis`
- Lucide 整体的 ISC 许可：`Copyright (c) 2026 Lucide Icons and Contributors`

两者都要求在任何副本中保留版权声明与许可声明。原文见下方。

<details>
<summary>MIT License（适用于 Lucide 的 terminal 图标）</summary>

```
The MIT License (MIT)

Copyright (c) 2013-present Cole Bemis

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
</details>

<details>
<summary>ISC License（Lucide 整体）</summary>

```
ISC License

Copyright (c) 2026 Lucide Icons and Contributors

Permission to use, copy, modify, and/or distribute this software for any
purpose with or without fee is hereby granted, provided that the above
copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
```
</details>

---

## 关于 DeepSeek Harness 品牌

本项目是**非官方**的第三方工具。依据 DeepSeek Harness 官方的
[BRAND_GUIDELINES.md](https://github.com/deepseek-ai/deepseek-harness/blob/master/BRAND_GUIDELINES.md)：

- 项目命名使用缩写 **DSH**（本仓库名 `DshLauncher`），未使用完整 "DeepSeek Harness" 商标；
- 描述性文字中如实说明「built on DeepSeek Harness」；
- 未把官方品牌素材当作本项目的官方标识使用。

> 注：官方规范要求「避免以可能引起误解的方式使用官方品牌素材，以免让用户产生官方背书、
> 合作或授权的错觉」。角色插画若本身派生自官方形象，同样应遵循这一条。

---

## 运行时组件（随单文件 exe 打包）

| 组件 | 许可证 |
|---|---|
| .NET 9 运行时 | MIT |
| Microsoft Edge WebView2 | Microsoft 软件许可条款 |
