# LocalDeck frontend

React + TypeScript UI rendered inside the LocalDeck WebView2 shell.

```powershell
npm ci
npm run build
npm run lint
```

The production build writes to `../src/LocalDeck.App/wwwroot` so it is included
in the desktop publish output.
