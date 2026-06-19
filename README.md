# Azure Blob Storage Inventory Automation

This project automatically generates a consolidated inventory report across all Azure Storage Accounts in your subscriptions. It leverages Azure Functions (Isolated Worker Model, .NET 8), Durable Functions, and Azure Resource Graph to discover, scan, and consolidate blob metadata into highly readable Excel and PDF reports.

## Features

- **Automated Discovery**: Uses Azure Resource Graph (with ARM fallback) to dynamically discover all storage accounts.
- **Parallel Scanning**: Uses Durable Functions Fan-out/Fan-in pattern to scan multiple storage accounts simultaneously.
- **Smart Filtering**: Ignores system containers (`$logs`, `$web`) and prevents recursive scanning of its own reports.
- **Consolidated Reporting**: Generates a unified Excel (.xlsx) file and a PDF summary report.
- **Scheduled Execution**: Integrates with Azure Logic Apps via an HTTP trigger for scheduled recurring runs.

## Architecture

1. **Logic App Trigger**: A Logic App invokes the `RunInventory` HTTP trigger on a recurring schedule.
2. **Orchestrator**: `StorageInventoryOrchestrator` starts the execution.
3. **Discovery**: `DiscoverAccountsActivity` queries Azure Resource Graph to find storage accounts.
4. **Scanning**: `ScanAccountActivity` is fanned out to scan containers and blobs concurrently, saving temporary JSON files to the warehouse account.
5. **Consolidation**: `GenerateConsolidatedReportsActivity` merges all JSON records and generates the final Excel and PDF reports, placing them in the `inventory-reports` container.

## Prerequisites

- .NET 8.0 SDK
- Azure Functions Core Tools
- An Azure Subscription with Owner/Contributor access.
- A "Warehouse" Storage Account to store the reports (e.g., `testconsumption92e8`).

## Local Setup

1. Clone or download this folder.
2. Ensure you have the `local.settings.json` file properly configured:
   ```json
   {
     "IsEncrypted": false,
     "Values": {
       "AzureWebJobsStorage": "UseDevelopmentStorage=true",
       "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
       "WAREHOUSE_ACCOUNT": "your_warehouse_account_name",
       "REPORT_CONTAINER": "inventory-reports"
     }
   }
   ```
3. Run the function app locally:
   ```bash
   func start
   ```

## Deployment

To deploy to your Azure Function App:
```bash
func azure functionapp publish <YourFunctionAppName>
```

## Permissions required in Azure

For this function to work properly in Azure, you must assign its **Managed Identity** the following roles:
- **Reader**: Over the subscription (to discover accounts).
- **Storage Blob Data Reader**: Over the subscription (to read blobs in all accounts).
- **Storage Blob Data Contributor**: Over the Warehouse Storage account (to write the consolidated reports).

## How to trigger

Once deployed, the function can be triggered via HTTP:
```
POST https://<YourFunctionAppName>.azurewebsites.net/api/run-inventory-orchestrator
```
This endpoint will return a HTTP 202 Accepted response and a `statusQueryGetUri` to check the progress of the durable orchestration.

## Output

Reports are generated inside the warehouse account in the `inventory-reports` container with the following naming convention:
- `YYYY/MM/DD/blob_inventory_consolidated_YYYYMMDD_HHMMSS.xlsx`
- `YYYY/MM/DD/blob_inventory_summary_consolidated_YYYYMMDD_HHMMSS.pdf`
