# Integrated Rebuild — UI Refresh Notes

This revision keeps the existing dark navy / cyan / violet palette and preserves commands, bindings, services, download/install paths, and navigation targets.

## UI changes
- The sidebar has more breathing room and a persistent AzaelX Discord invite CTA that opens `https://discord.gg/e2MXnGbXf2` in the default browser.
- Settings now displays the Discord avatar URL already stored by `AuthService`, alongside the account name and email.
- The Add catalog uses poster-style cards for Top Sellers and compact landscape tiles for New Releases, creating a distinct visual rhythm without changing the Steam catalog or selection commands.
- Page margins, hero surfaces, card sizes, and corner treatment were refreshed across Home, Manage, Builds, Mode, Fixes, Downloads, and Settings.
- The previous “Community” section label is now “Background Services”. The key-sharing control remains explicit and opt-out-capable, with wording that describes reading `config.vdf` and sending newly validated depot keys. This intentionally avoids concealing a data-transfer behavior from the people using the app.

## Validation
- XAML/resources XML parse and localization key parity should be checked with `python scripts/check-i18n.py`.
- This environment does not have the .NET SDK installed, so a Windows WPF `dotnet build`/`dotnet test` was not run here. Recommended verification on Windows:

```powershell
dotnet build .\LuaToolsGui.sln -c Release -m:1
dotnet test .\LuaToolsGui.sln -c Release -m:1
```


## Top Sellers expansion
- The Add page now fills its Top Sellers catalog from Steam's featured category first, then best-effort supplements from Steam's public global-top-sellers search endpoint when fewer candidates are returned.
- Duplicate App IDs are removed; hardware App IDs are still filtered by the existing `HardwareAppIdService`.
- The visible Top Sellers section now displays up to 12 games. New Releases, game selection, and the existing install/download pipeline are unchanged.
- The Background Services description and explicit control remain visible. The keys-sharing behavior is not concealed from app users.
