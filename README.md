# AccordIn

**AI-native account planning intelligence for Microsoft Dynamics 365**

AccordIn is an account planning copilot built natively on D365 CE and Power Platform. It surfaces account planning intelligence that account managers miss — primary relationship contacts, stage-based opportunity prioritisation, approval risk gaps, and grounded revenue forecasting — through a conversational interface embedded directly in the CRM.

## Architecture

- **Model layer**: Fine-tuned GPT-4o on Azure AI Foundry, with pre-calculated pipeline injection and contact role enrichment
- **Service layer**: Node.js backend exposing the copilot API
- **CRM layer**: D365 CE with custom Dataverse tables, HTML web resource hub, Power Automate flows
- **UI layer**: AccordIn Hub (HTML web resource) with plan canvas, contact engagement section, and copilot chat

## Repository Structure

| Folder | Contents |
|--------|----------|
| `model/` | System prompts, training data, evaluation scenarios |
| `copilot-service/` | Node.js backend API |
| `frontend/` | React evaluation app |
| `power-platform/` | D365 solution, web resources, flows, data model |
| `docs/` | Architecture decisions, article drafts |
| `sample-data/` | Demo account scenarios |

## Key Innovations

1. Pre-calculated pipeline injection — LLM reasons, never computes
2. Stage-weighted forecasting with verified Low/Mid/High scenarios
3. Primary relationship contact intelligence via fine-tuned model
4. Contact engagement layer with planRole classification and coverage gap warnings
5. Native CRM orchestration — no third-party overlay
6. Conversational plan refinement with live re-render
