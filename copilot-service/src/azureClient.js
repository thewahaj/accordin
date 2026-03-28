const { AzureOpenAI } = require('openai');

let client = null;

function getClient() {
  if (!client) {
    if (!process.env.AZURE_OPENAI_ENDPOINT || !process.env.AZURE_OPENAI_API_KEY) {
      throw new Error('Azure OpenAI credentials not configured. Check your .env file.');
    }
    client = new AzureOpenAI({
      endpoint: process.env.AZURE_OPENAI_ENDPOINT,
      apiKey: process.env.AZURE_OPENAI_API_KEY,
      apiVersion: process.env.AZURE_OPENAI_API_VERSION || '2024-02-01',
      deployment: process.env.AZURE_OPENAI_DEPLOYMENT_NAME || 'gpt-4o',
    });
  }
  return client;
}

async function callModel(messages, maxTokens = 2000) {
  const c = getClient();
  const response = await c.chat.completions.create({
    model: process.env.AZURE_OPENAI_DEPLOYMENT_NAME || 'gpt-4o',
    messages,
    max_tokens: maxTokens,
    temperature: 0.3,
  });
  return response.choices[0].message.content;
}

function parseModelResponse(rawText) {
  try {
    const cleaned = rawText
      .replace(/^```json\s*/i, '')
      .replace(/^```\s*/i, '')
      .replace(/```\s*$/i, '')
      .trim();
    return { parsed: JSON.parse(cleaned), raw: rawText, parseError: null };
  } catch (e) {
    return { parsed: null, raw: rawText, parseError: e.message };
  }
}

module.exports = { callModel, parseModelResponse };
