using MudBlazor;
using SbConsole.Core.Settings;
using SbConsole.Sdk;
using SbConsole.Web.Components.Shared;

namespace SbConsole.Web.Confirmation;

public sealed class MudConfirmationService(IDialogService dialogService, ISettings settings) : IConfirmationService, IScopedService
{
    public async Task<bool> ConfirmAsync(string verb, string target, bool isProd, int? count = null, CancellationToken ct = default)
    {
        var requireTyped = await RequireTypedAsync(isProd, ct);

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

    public async Task<bool> ConfirmAsync(ConfirmationRequest request, CancellationToken ct = default)
    {
        var requireTyped = await RequireTypedAsync(request.IsProd, ct);

        var parameters = new DialogParameters<ConfirmDialog>
        {
            { x => x.Verb, request.Verb },
            { x => x.Target, request.Target },
            { x => x.RequireTypedConfirmation, requireTyped },
            { x => x.IsProd, request.IsProd },
            { x => x.Consequence, request.Consequence },
            { x => x.Details, request.Details },
            { x => x.AuditAction, request.AuditAction },
            { x => x.Destructive, request.Destructive },
            { x => x.Detailed, true },
        };

        // The detailed title is the bare verb (the PROD marker sits in the content header).
        var dialog = await dialogService.ShowAsync<ConfirmDialog>(request.Verb, parameters);
        var result = await dialog.Result;
        return result is { Canceled: false };
    }

    private async Task<bool> RequireTypedAsync(bool isProd, CancellationToken ct)
    {
        var settingValue = await settings.GetAsync("confirm.requireTypedForProd", ct);
        var requireTypedForProd = !bool.TryParse(settingValue, out var parsed) || parsed;
        return isProd && requireTypedForProd;
    }
}
