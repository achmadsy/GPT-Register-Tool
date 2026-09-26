// Opted into nullable reference checking file-by-file - see the note in
// PaymentBatchService.cs for why the project-wide switch stays `annotations`.
#nullable enable

using System.Windows.Input;

namespace SmsWorkbench
{
    public sealed partial class ProtocolPaymentViewModel : ObservableObject, IDisposable
    {
        private readonly IProtocolPaymentService _service;
        // Null whenever no run is in flight - created in RunAsync, disposed and
        // cleared in the finally block. Cancel() reads `_cancellation == null`
        // as "nothing running", so the null is part of the state machine and not
        // an accident to paper over with `= null!`.
        private CancellationTokenSource? _cancellation;
        private string _lastUrl = "";
        private string _lastQrPath = "";

        public ProtocolPaymentViewModel(
            IProtocolPaymentService service,
            IFileLauncher fileLauncher,
            ProtocolPaymentAccount? account,
            // Optional: null means "run without a stage matrix store"; the view
            // model is constructed that way for manual (account-less) runs.
            IStageMatrixStore? stageMatrixStore = null)
        {
            _service = service;
            FileLauncher = fileLauncher;
            Account = account;
            StageMatrix = new StageMatrixViewModel(stageMatrixStore);
            ProtocolPaymentPreferences preferences = service.LoadPreferences();
            Methods = PaymentMethods.All.ToArray();
            BillingCountries = PaymentMethods.BillingCountryOptions;
            SelectedMethod = Methods.FirstOrDefault(method => method.Id == PaymentMethods.Normalize(preferences.Method)) ?? Methods[0];
            TargetCountry = ResolveBilling(preferences.TargetCountry, SelectedMethod.DefaultCountry);
            LoadCountriesAndProxyConfiguration(true);

            RunCommand = new AsyncRelayCommand(RunAsync, () => !IsRunning);
            TestProxyCommand = new AsyncRelayCommand(TestProxyAsync, () => !IsRunning);
            SaveProxyCommand = new RelayCommand(SaveProxy, () => !IsRunning);
            CancelCommand = new RelayCommand(Cancel, () => IsRunning);
            CopyCommand = new RelayCommand(CopyResult, () => HasUrl);
            OpenQrCommand = new RelayCommand(OpenQr, () => HasQr);
        }

        public IFileLauncher FileLauncher { get; }
        // Null for a manual (account-less) run - IsManual below is literally
        // "Account == null", so the type has to allow it.
        public ProtocolPaymentAccount? Account { get; }
        public bool IsManual => Account == null;
        public bool IsSelectedAccount => Account != null;
        public IReadOnlyList<PaymentMethodDefinition> Methods { get; }
        public IReadOnlyList<PaymentProxyCountryOption> BillingCountries { get; }
        public IReadOnlyList<PaymentProxyCountryOption> CheckoutCountries { get; private set; } = Array.Empty<PaymentProxyCountryOption>();
        public IReadOnlyList<PaymentProxyCountryOption> ApproveCountries { get; private set; } = Array.Empty<PaymentProxyCountryOption>();
        public StageMatrixViewModel StageMatrix { get; }
        public ICommand RunCommand { get; }
        public ICommand TestProxyCommand { get; }
        public ICommand SaveProxyCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand CopyCommand { get; }
        public ICommand OpenQrCommand { get; }

        [ObservableProperty] private PaymentMethodDefinition selectedMethod;
        [ObservableProperty] private string manualAccessToken = "";
        [ObservableProperty] private string targetCountry = "US";
        [ObservableProperty] private string checkoutProxyPool = "";
        [ObservableProperty] private string approveProxyPool = "";
        [ObservableProperty] private string checkoutCountry = "";
        [ObservableProperty] private string approveCountry = "";
        [ObservableProperty] private string updateCountry = "";
        [ObservableProperty] private string blikCode = "";
        [ObservableProperty] private bool jitRefresh = true;
        [ObservableProperty] private bool probeOnly;
        [ObservableProperty] private bool requireZero = true;
        [ObservableProperty] private bool requireBaToken = true;
        [ObservableProperty] private bool isRunning;
        [ObservableProperty] private string resultText = "";
        [ObservableProperty] private string statusText = "Ready";

        public bool ShowManualToken => IsManual;
        public bool ShowJitAndProbe => IsSelectedAccount;
        public bool ShowProbeOnly => IsSelectedAccount || IsOfflineValidationOnly;
        public bool ShowBlickCode => SelectedMethod?.Id == "blik";
        public bool ShowStageCountries => SelectedMethod != null;
        public bool IsOfflineValidationOnly => SelectedMethod?.Adapter == "regional_wallet";
        public bool CanToggleProbeOnly => ShowProbeOnly;
        public string RunActionText => ProbeOnly ? "Start probe" : SelectedMethod?.Id == "blik" ? "Run payment" : "Extract link";
        public bool CanRequireBa => SelectedMethod?.Id == "paypal" && !ProbeOnly;
        public bool CanRequireZero => !ProbeOnly;
        public bool CanEditUpdateCountry => SelectedMethod?.Id is "paypal" or "gopay" or "direct_card";
        public bool HasUrl => _lastUrl.Length > 0;
        public bool HasQr => _lastQrPath.Length > 0 && FileLauncher.Exists(_lastQrPath);
        public string AccountLabel => Account == null ? "Manual Access Token" : Account.Email;

        partial void OnSelectedMethodChanged(PaymentMethodDefinition value)
        {
            if (value == null) return;
            TargetCountry = ResolveBilling(TargetCountry, value.DefaultCountry);
            LoadCountriesAndProxyConfiguration(true);
            OnPropertyChanged(nameof(ShowBlickCode));
            OnPropertyChanged(nameof(ShowStageCountries));
            OnPropertyChanged(nameof(IsOfflineValidationOnly));
            OnPropertyChanged(nameof(CanToggleProbeOnly));
            OnPropertyChanged(nameof(ShowProbeOnly));
            OnPropertyChanged(nameof(CanRequireBa));
            OnPropertyChanged(nameof(CanEditUpdateCountry));
            OnPropertyChanged(nameof(RunActionText));
        }

        partial void OnProbeOnlyChanged(bool value)
        {
            OnPropertyChanged(nameof(CanRequireBa));
            OnPropertyChanged(nameof(CanRequireZero));
            OnPropertyChanged(nameof(RunActionText));
        }

        partial void OnIsRunningChanged(bool value)
        {
            (RunCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
            (TestProxyCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
            (SaveProxyCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (CancelCommand as RelayCommand)?.NotifyCanExecuteChanged();
        }

        private void LoadCountriesAndProxyConfiguration(bool loadCountries)
        {
            CheckoutCountries = PaymentMethods.CheckoutCountryOptions(SelectedMethod.Id);
            ApproveCountries = PaymentMethods.ApproveCountryOptions(SelectedMethod.Id);
            OnPropertyChanged(nameof(CheckoutCountries));
            OnPropertyChanged(nameof(ApproveCountries));
            PaymentBatchProxyConfiguration configured = _service.LoadProxyConfiguration(SelectedMethod.Id);
            CheckoutProxyPool = configured.CheckoutProxyPool ?? "";
            ApproveProxyPool = configured.ApproveProxyPool ?? "";
            CheckoutCountry = SelectCountry(configured.CheckoutCountry, CheckoutCountries, SelectedMethod.DefaultCountry);
            ApproveCountry = SelectCountry(configured.ApproveCountry, ApproveCountries, SelectedMethod.DefaultCountry);
            UpdateCountry = SelectCountry(configured.UpdateCountry, ApproveCountries, PaymentMethods.DefaultUpdateCountry(SelectedMethod.Id, SelectedMethod.DefaultCountry));
        }

        private async Task TestProxyAsync()
        {
            IsRunning = true;
            StatusText = "Testing checkout / approve / update proxy egress...";
            try
            {
                ResultText = await _service.TestProxiesAsync(
                    SelectedMethod.Id,
                    new PaymentBatchProxyConfiguration(CheckoutProxyPool, ApproveProxyPool, CheckoutCountry, ApproveCountry, UpdateCountry),
                    CancellationToken.None);
                StatusText = "Proxy probe completed";
            }
            catch (Exception exception)
            {
                ResultText = "[Error] " + SensitiveDataSanitizer.Redact(exception.Message);
                StatusText = "Proxy probe failed";
            }
            finally
            {
                IsRunning = false;
            }
        }

        private void SaveProxy()
        {
            SettingsSaveResult result = _service.SaveProxyConfiguration(
                SelectedMethod.Id,
                new PaymentBatchProxyConfiguration(CheckoutProxyPool, ApproveProxyPool, CheckoutCountry, ApproveCountry, UpdateCountry));
            ResultText = result.Ok
                ? "[Success] Saved the Checkout / Approve-Update proxy pool for the current payment method."
                : "[Failed] " + result.Error;
        }

        private async Task RunAsync()
        {
            if (IsOfflineValidationOnly && !ProbeOnly)
            {
                ResultText = "This regional method currently only supports standalone-adapter offline contract validation; no production transport is configured. Enable the capability probe.";
                StatusText = "Offline validation only";
                return;
            }
            if (IsManual && string.IsNullOrWhiteSpace(ManualAccessToken))
            {
                ResultText = "Enter an Access Token";
                return;
            }
            if (!ProbeOnly && SelectedMethod.Id == "blik"
                && (BlikCode.Trim().Length != 6 || !BlikCode.Trim().All(char.IsDigit)))
            {
                ResultText = "Enter a valid 6-digit BLIK code";
                return;
            }

            SavePreferences();
            StageMatrix.Reset();
            IsRunning = true;
            _cancellation = new CancellationTokenSource();
            _lastUrl = "";
            _lastQrPath = "";
            OnPropertyChanged(nameof(HasUrl));
            OnPropertyChanged(nameof(HasQr));
            StatusText = ProbeOnly ? "Running Checkout / Stripe init capability probe..." : "Running protocol payment...";
            var progress = new Progress<BackendOutputLine>(line =>
            {
                if (BackendProgressEventParser.TryParse(line.Text, out BackendProgressEvent? progressEvent))
                {
                    StageMatrix.Apply(progressEvent);
                    StatusText = progressEvent.Detail.Length > 0 ? progressEvent.Detail : progressEvent.Stage;
                }
            });
            try
            {
                ProtocolPaymentRunResult outcome = await _service.RunAsync(
                    new ProtocolPaymentRequest(
                        SelectedMethod.Id,
                        ManualAccessToken,
                        TargetCountry,
                        CheckoutProxyPool,
                        ApproveProxyPool,
                        JitRefresh,
                        ProbeOnly,
                        RequireZero,
                        RequireBaToken,
                        BlikCode,
                        CheckoutCountry,
                        ApproveCountry,
                        UpdateCountry,
                        Account),
                    progress,
                    _cancellation.Token);
                ResultText = outcome.Presentation.Text;
                _lastUrl = outcome.Presentation.Url ?? "";
                _lastQrPath = outcome.Presentation.QrPath ?? "";
                StatusText = outcome.Error.Length > 0 ? "Run failed" : "Finished";
                OnPropertyChanged(nameof(HasUrl));
                OnPropertyChanged(nameof(HasQr));
                (CopyCommand as RelayCommand)?.NotifyCanExecuteChanged();
                (OpenQrCommand as RelayCommand)?.NotifyCanExecuteChanged();
            }
            finally
            {
                _cancellation.Dispose();
                _cancellation = null;
                IsRunning = false;
            }
        }

        private void Cancel()
        {
            if (_cancellation == null) return;
            StatusText = "Cancelling protocol payment run...";
            _cancellation.Cancel();
        }

        public void Dispose()
        {
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _cancellation = null;
            GC.SuppressFinalize(this);
        }

        private void CopyResult()
        {
            if (!HasUrl) return;
            Clipboard.SetText(_lastUrl);
            StatusText = "Payment link copied";
        }

        private void OpenQr()
        {
            if (!HasQr) return;
            FileLauncher.Open(_lastQrPath);
        }

        private void SavePreferences()
        {
            _service.SavePreferences(new ProtocolPaymentPreferences
            {
                Method = SelectedMethod.Id,
                TargetCountry = TargetCountry,
                CheckoutCountry = CheckoutCountry,
                ApproveCountry = ApproveCountry,
                UpdateCountry = UpdateCountry
            });
        }

        private string ResolveBilling(string wanted, string fallback)
            => BillingCountries.Any(country => country.Code.Equals((wanted ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                ? BillingCountries.First(country => country.Code.Equals(wanted.Trim(), StringComparison.OrdinalIgnoreCase)).Code
                : fallback;

        private static string SelectCountry(string wanted, IReadOnlyList<PaymentProxyCountryOption> options, string fallback)
        {
            // `?.Code` makes both lookups nullable - FirstOrDefault yields null
            // when nothing matches. The final `??` is what collapses that to a
            // real country code, so the local has to be nullable too.
            string? selected = options.FirstOrDefault(option => option.Code.Equals((wanted ?? "").Trim(), StringComparison.OrdinalIgnoreCase))?.Code
                ?? options.FirstOrDefault(option => option.Code.Equals(fallback, StringComparison.OrdinalIgnoreCase))?.Code;
            return selected ?? (options.Count > 0 ? options[0].Code : "");
        }
    }
}
