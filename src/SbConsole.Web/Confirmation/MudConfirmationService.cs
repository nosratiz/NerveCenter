using MudBlazor;
using SbConsole.Core.Settings;
using SbConsole.Sdk;
using SbConsole.Web.Components.Shared;

namespace SbConsole.Web.Confirmation;

public sealed class MudConfirmationService(IDialogService dialogService, ISettings settings) : IConfirmationService
{
    public async Task<bool> ConfirmAsync(string verb, string target, bool isProd, int? count = null, CancellationToken ct = default)
    {
        var settingValue = await settings.GetAsync("confirm.requireTypedForProd", ct);
        var requireTypedForProd = !bool.TryParse(settingValue, out var parsed) || parsed;
        var requireTyped = isProd && requireTypedForProd;

        var parameters = new DialogParameters<ConfirmDialog>
        {
            { x => x.Verb, verb },
            { x => x.Target, target },
            { x => x.Count, count },
            { x => x.RequireTypedConfirmation, requireTyped },
        };

        var dialog = await dialogService.ShowAsync<ConfirmDialog>($"{verb} {target}?", parameters);
        var result = await dialog.Result;
        return result is { Canceled: false };
    }
}
