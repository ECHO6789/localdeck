# Shortcuts and macros

## Global shortcuts

The shortcut center uses `RegisterHotKey`, so actions work while LocalDeck is in
the tray or another application is focused.

Built-in IDs:

- `screenshot`: capture a region and copy it
- `pin-image`: pin the current clipboard image
- `screenshot-pin`: capture a region and pin it immediately
- `clipboard`: open clipboard history
- `search`: focus local file search
- `panel`: show LocalDeck
- `proxy-toggle`: start or stop the local forwarding listener without changing
  the Windows system proxy
- `macro-editor`: open the standalone macro studio window
- `launcher`: open the executable launcher
- `overlay`: toggle the native performance overlay

The default launcher shortcut is `Ctrl+Alt+E`; `Ctrl+Alt+L` was observed to be
owned by another application during verification.

The default performance overlay shortcut is `Ctrl+Alt+P`. Exact FPS starts the
built-in elevated FPS helper on demand; the main LocalDeck window stays
non-elevated.

Custom actions support showing LocalDeck/clipboard, screenshot, pin image,
proxy toggle, running a macro, opening a file/URL/command, typing text, and
locking Windows. Registration conflicts are reported without crashing.

Snipaste-style defaults are `F1` for screenshot and `F3` for pinning a clipboard
image. If Snipaste still owns those combinations, LocalDeck shows a conflict;
close or uninstall Snipaste and press “保存并重新注册”.

## Side buttons and macros

`MacroService` installs a `WH_MOUSE_LL` hook. When `XButton1` or `XButton2` has a
binding, both the down and up events are swallowed and the configured macro is
executed.

Supported steps:

- key combination
- Unicode text
- mouse click
- local program, URL, or command
- delay

Macro execution uses Windows `SendInput`. A non-reentrant guard avoids duplicate
runs while a macro is still executing.

## Reusable frontend component

[`frontend/src/components/MacroEditor.tsx`](https://github.com/ECHO6789/localdeck/blob/main/frontend/src/components/MacroEditor.tsx)
is a controlled React component. It accepts:

```ts
{
  macros: MacroDefinition[]
  bindings: Record<string, string>
  disabled?: boolean
  onChange: (macros: MacroDefinition[]) => void
  onBindingsChange: (bindings: Record<string, string>) => void
}
```

The macro page stores the resulting objects through the same `AppSettings`
bridge used by the rest of LocalDeck.

## Macro studio window

`Ctrl+Alt+M` opens a separate WebView2 window. It reuses
`MacroEditor.tsx` and the same bridge/settings model, so macros created in the
main window and the standalone window are immediately interchangeable.

The recorder installs low-level keyboard and mouse hooks only while recording.
It captures keyboard combinations, text-producing keys, physical mouse buttons,
side buttons and click coordinates. Press `F8` or the in-window stop button to
finish. Injected events are ignored to avoid recording LocalDeck's own output.

Macro steps support:

- key combination
- Unicode text
- mouse click
- coordinate click
- local program, URL or command
- delay in milliseconds

Only one macro executes at a time; a non-reentrant guard prevents a macro from
recursing into itself.
