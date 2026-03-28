const express = require('express');
const router = express.Router();
const { getIntents, getScenarios, loadScenario } = require('../scenarioService');

router.get('/', (req, res) => {
  try {
    res.json(getIntents());
  } catch (e) {
    res.status(500).json({ error: e.message });
  }
});

router.get('/:intent/scenarios', (req, res) => {
  try {
    res.json(getScenarios(req.params.intent));
  } catch (e) {
    res.status(500).json({ error: e.message });
  }
});

router.get('/:intent/scenarios/:scenario', (req, res) => {
  try {
    res.json(loadScenario(req.params.intent, req.params.scenario));
  } catch (e) {
    res.status(404).json({ error: e.message });
  }
});

module.exports = router;
