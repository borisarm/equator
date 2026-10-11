using Windows.Services.Store;

namespace Equator.App.Services;

public enum LicenseState { Full, Trial, Expired }

/// <summary>
/// The Microsoft Store license. The 7-day free trial is configured in Partner Center (Pricing and availability →
/// Free trial); the Store tracks the trial period and the purchase, and keeps an offline copy of the license, so
/// this needs no server or network code of its own. While the trial runs everything works; once it expires,
/// notebooks still open, edit and save, but cells no longer evaluate until the app is bought.
/// Builds that did not come from the Store (dotnet run, the portable publish, a locally registered MSIX) have no
/// Store license and run unrestricted; EQUATOR_LICENSE=trial:&lt;days&gt;|expired simulates the Store states there.
/// </summary>
public sealed class Licensing
{
    /// <summary>The app's Store ID (Partner Center → Product identity).</summary>
    private const string StoreId = "9P7WTJNFC7J8";

    private readonly StoreContext? _context;
    private readonly string? _simulated;

    public LicenseState State { get; private set; } = LicenseState.Full;

    /// <summary>Whole days left in the trial, rounded up (1 on the last day).</summary>
    public int TrialDaysLeft { get; private set; }

    public bool CanEvaluate => State != LicenseState.Expired;

    /// <summary>Raised on a background thread when Windows reports a license change (for example a purchase).</summary>
    public event EventHandler? Changed;

    public Licensing(nint windowHandle)
    {
        if (!InstalledFromStore())
        {
            _simulated = Environment.GetEnvironmentVariable("EQUATOR_LICENSE");
            return;
        }
        _context = StoreContext.GetDefault();
        // Desktop apps must give the Store an owner window for its purchase dialog.
        WinRT.Interop.InitializeWithWindow.Initialize(_context, windowHandle);
        _context.OfflineLicensesChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool InstalledFromStore()
    {
        try
        {
            return Windows.ApplicationModel.Package.Current.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.Store;
        }
        catch (InvalidOperationException)
        {
            return false; // unpackaged
        }
    }

    /// <summary>Reads the current license from the Store (served from its offline cache when there is no network).</summary>
    public async Task RefreshAsync()
    {
        if (_context is null)
        {
            (State, TrialDaysLeft) = _simulated?.Split(':') switch
            {
                ["expired"] => (LicenseState.Expired, 0),
                ["trial", var d] when int.TryParse(d, out var days) => (LicenseState.Trial, Math.Max(1, days)),
                _ => (LicenseState.Full, 0),
            };
            return;
        }
        try
        {
            var license = await _context.GetAppLicenseAsync();
            if (!license.IsActive)
                (State, TrialDaysLeft) = (LicenseState.Expired, 0);
            else if (license.IsTrial)
                (State, TrialDaysLeft) = (LicenseState.Trial, Math.Max(1, (int)Math.Ceiling(license.TrialTimeRemaining.TotalDays)));
            else
                (State, TrialDaysLeft) = (LicenseState.Full, 0);
            App.Log($"license: {State}, trial days left {TrialDaysLeft}");
        }
        catch (Exception ex)
        {
            // Never lock out a paying user because the Store service misbehaved; the next refresh tries again.
            App.Log("license check failed: " + ex.Message);
            (State, TrialDaysLeft) = (LicenseState.Full, 0);
        }
    }

    /// <summary>Opens the Store purchase dialog. Returns true when the app is now bought.</summary>
    public async Task<bool> PurchaseAsync()
    {
        if (_context is null)
        {
            App.Log("purchase requested outside a Store install; simulating success");
            (State, TrialDaysLeft) = (LicenseState.Full, 0);
            return true;
        }
        try
        {
            var result = await _context.RequestPurchaseAsync(StoreId);
            App.Log($"purchase: {result.Status} {result.ExtendedError?.Message}");
            await RefreshAsync();
            return State == LicenseState.Full;
        }
        catch (Exception ex)
        {
            App.Log("purchase failed: " + ex.Message);
            return false;
        }
    }
}
