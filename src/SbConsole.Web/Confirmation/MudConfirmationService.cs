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
        var hasDetails = request.Details is { Count: > 0 };

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
        };

        // With details the title is the bare verb (the PROD chip sits in the content header).
        var title = hasDetails ? request.Verb : $"{request.Verb} {request.Target}?";
        var dialog = await dialogService.ShowAsync<ConfirmDialog>(title, parameters);
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
