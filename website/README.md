# equatorsol.es website

Static site for EquatorSolver: home, privacy policy and support pages in English (`/`) and Spanish (`/es/`).
No build step and no scripts; `staticwebapp.config.json` gives clean URLs (`/privacy` serves `privacy.html`),
redirects, the 404 page and security headers.

## Hosting

Azure Static Web Apps, Free plan:

- Subscription `equator_es`, resource group `rg-equatorsol-web` (West Europe), app `equatorsol-web`
- Default address: https://green-bay-06ae3f903.4.azurestaticapps.net

## Deploying

```powershell
az login --tenant 6e1f1402-3fe3-4140-8e33-48ade651dfbb
az account set --subscription equator_es
$env:SWA_CLI_DEPLOYMENT_TOKEN = az staticwebapp secrets list --name equatorsol-web --resource-group rg-equatorsol-web --query "properties.apiKey" -o tsv
npx @azure/static-web-apps-cli deploy ./website --env production
Remove-Item Env:SWA_CLI_DEPLOYMENT_TOKEN
```

Run it from the repository root. The deployment token is a secret: never commit it.

## Custom domain

At the DNS provider (GoDaddy), keep the mail records (MX, SPF/DKIM TXT) as they are and add:

| Type | Name | Value |
| --- | --- | --- |
| CNAME | `www` | `green-bay-06ae3f903.4.azurestaticapps.net` |

Then register the name with the app (Azure validates the CNAME and issues the HTTPS certificate):

```powershell
az staticwebapp hostname set --name equatorsol-web --resource-group rg-equatorsol-web --hostname www.equatorsol.es
```

GoDaddy cannot point the bare domain (`equatorsol.es`) at Azure with a CNAME or ALIAS record, so use GoDaddy's
domain forwarding: `equatorsol.es` → `https://www.equatorsol.es`, permanent (301), without masking.

## Updating

- The Store link points to a Store search for "EquatorSolver"; once the app is published, replace it in
  `index.html` and `es/index.html` with the app's page (`https://apps.microsoft.com/detail/<Store ID>`).
- Screenshots in `img/` are copies of `store/screenshots/en` and `store/screenshots/es`.
- Change the "Last updated" date on both privacy pages whenever the policy changes.
