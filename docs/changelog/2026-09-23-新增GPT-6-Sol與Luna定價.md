# 補上 GPT-6 Sol 與 Luna 計費設定（0.9.54）

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：0.9.54
- 首次實作版本：0.9.54
- 最後核對日期：2026/09/23

## 官方費率核對

以下為 OpenAI API Standard 每百萬 token 的美元價格；長脈絡分級在單次請求的輸入 token **超過 272,000** 時對整筆請求生效。

| 模型 | 一般：輸入／快取輸入／輸出 | 長脈絡：輸入／快取輸入／輸出 |
|---|---|---|
| `gpt-5.6-sol` | 4／0.4／20 | 8／0.8／30 |
| `gpt-5.6-terra` | 2／0.2／12 | 4／0.4／18 |
| `gpt-6-sol` | 2／0.2／10 | 4／0.4／15 |
| `gpt-6-luna` | 0.1／0.01／0.5 | 0.2／0.02／0.75 |

原有兩個 GPT-5.6 模型的費率與門檻均符合官方資料，本次保留。新增兩個 GPT-6 模型至 `AiPricingSettings:Models`，並將設定測試與操作文件中舊的 128,000 門檻敘述同步改為 272,000。`SystemVersion` 由 `0.9.53` 遞增為 `0.9.54`。

來源：[GPT-5.6 Sol](https://developers.openai.com/api/docs/models/gpt-5.6-sol)、[GPT-5.6 Terra](https://developers.openai.com/api/docs/models/gpt-5.6-terra)、[GPT-6 Sol](https://developers.openai.com/api/docs/models/gpt-6-sol)、[GPT-6 Luna](https://developers.openai.com/api/docs/models/gpt-6-luna)、[OpenAI API 定價](https://developers.openai.com/api/docs/pricing)。`gpt-5.6-sol` 的優惠價格，官方標示至少持續至 2026/11/21。

本費率表估算 OpenAI API Standard 文字 token 費用。現有用量資料無法可靠區分 cache writes、Batch、Flex 或 Fast 模式；若透過 Azure OpenAI 使用，仍須對照實際供應者帳單。
