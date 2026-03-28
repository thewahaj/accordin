require('dotenv').config();
const express = require('express');
const cors = require('cors');
const intentRoutes = require('./routes/intents');
const runRoutes = require('./routes/run');
const chatRoutes = require('./routes/chat');

const app = express();
const PORT = process.env.PORT || 3001;

app.use(cors({ origin: 'http://localhost:3000' }));
app.use(express.json());

app.use('/api/intents', intentRoutes);
app.use('/api/run', runRoutes);
app.use('/api/chat', chatRoutes);

app.get('/api/health', (req, res) => {
  res.json({
    status: 'ok',
    endpoint: process.env.AZURE_OPENAI_ENDPOINT ? 'configured' : 'missing',
    deployment: process.env.AZURE_OPENAI_DEPLOYMENT_NAME || 'missing'
  });
});

app.use((err, req, res, next) => {
  console.error(err.stack);
  res.status(500).json({ error: err.message || 'Internal server error' });
});

app.listen(PORT, () => {
  console.log(`Backend running on http://localhost:${PORT}`);
  console.log(`Azure endpoint: ${process.env.AZURE_OPENAI_ENDPOINT || 'NOT SET'}`);
  console.log(`Deployment: ${process.env.AZURE_OPENAI_DEPLOYMENT_NAME || 'NOT SET'}`);
});
