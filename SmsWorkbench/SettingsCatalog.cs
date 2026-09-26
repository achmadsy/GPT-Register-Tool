// Opted into nullable reference checking file-by-file - see the note in
// PaymentBatchService.cs for why the project-wide switch stays `annotations`.
#nullable enable

namespace SmsWorkbench
{
    public sealed record SettingsSectionDefinition(string Title, IReadOnlyList<SettingDefinition> Fields);
    public sealed record SettingsCategoryDefinition(string Title, IReadOnlyList<SettingsSectionDefinition> Sections);

    public static class SettingsCatalog
    {
        /// <summary>
        /// Default SMS provider key. Mirrors `sms_providers.DEFAULT_PROVIDER`
        /// on the Python side; the two are pinned together by
        /// tests/test_settings_catalog_provider_parity.py.
        /// </summary>
        public const string DefaultPhoneProvider = "smsbower";

        private static SettingDefinition Text(string key, string label, string path, string fallback = "")
            => new(key, label, path, SettingFieldKind.Text, fallback);
        private static SettingDefinition Secret(string key, string label, string path)
            => new(key, label, path, SettingFieldKind.Secret);
        private static SettingDefinition Integer(string key, string label, string path, string fallback = "")
            => new(key, label, path, SettingFieldKind.Number, fallback);
        private static SettingDefinition Boolean(string key, string label, string path, bool fallback)
            => new(key, label, path, SettingFieldKind.Boolean, fallback ? "true" : "false");
        private static SettingDefinition Options(string key, string label, string path, string fallback, params string[] options)
            => new(key, label, path, SettingFieldKind.Options, fallback, options);
        private static SettingDefinition Multiline(string key, string label, string path, string fallback = "")
            => new(key, label, path, SettingFieldKind.Multiline, fallback);
        private static SettingsSectionDefinition Section(string title, params SettingDefinition[] fields) => new(title, fields);
        private static SettingsCategoryDefinition Category(string title, params SettingsSectionDefinition[] sections) => new(title, sections);

        public static IReadOnlyList<SettingsCategoryDefinition> Categories { get; } = new[]
        {
            Category("Mailboxes & Inbox",
                Section("Mailbox pool",
                    Integer("otp_poll_interval", "OTP polling interval (s)", "email_registration.otp_poll_interval"),
                    Text("token_file", "Mailbox pool file", "email_registration.token_file")),
                Section("ReMail",
                    Boolean("remail_enabled", "Enabled", "email_registration.remail.enabled", true),
                    Text("remail_base_url", "API URL", "email_registration.remail.base_url", "https://remail.aishop6.com"),
                    Secret("remail_api_key", "API Key", "email_registration.remail.api_key"),
                    Integer("remail_project_id", "Project ID", "email_registration.remail.project_id", "2"),
                    Options("remail_supply", "Supply strategy", "email_registration.remail.supply", "private_first", "private_first", "public_only"),
                    Text("remail_email_suffix", "Email suffix", "email_registration.remail.email_suffix", "outlook.com")),
                Section("Smailr",
                    Boolean("smailr_enabled", "Enabled", "email_registration.smailr.enabled", false),
                    Text("smailr_base_url", "API URL", "email_registration.smailr.base_url", "https://smailr.com"),
                    Secret("smailr_api_key", "API Key", "email_registration.smailr.api_key"),
                    Options("smailr_default_domain", "Email domain (LV1+)", "email_registration.smailr.default_domain", "smailr.com",
                        "smailr.com", "loc.cc", "mail.nodeloc.cc", "nodeloc.cc"),
                    Integer("smailr_timeout", "Request timeout (s)", "email_registration.smailr.timeout", "30")),
                Section("CFWorker",
                    Text("cfworker_url", "Worker URL", "email_registration.cfworker_url"),
                    Text("cfworker_domain", "Email domain", "email_registration.cfworker_domain"),
                    Secret("cfworker_admin_token", "Admin Token", "email_registration.cfworker_admin_token"),
                    Secret("cfworker_api_token", "Cloudflare API Token", "email_registration.cfworker_api_token"))),

            Category("Registration & SMS",
                Section("Registration driver",
                    Options("registration_driver", "Registration driver", "registration.driver", "protocol",
                        "protocol", "playwright", "roxy", "cloak", "camoufox"),
                    Boolean("registration_browser_headless", "Headless browser", "registration.browser_headless", true),
                    Integer("registration_browser_timeout", "Browser timeout (s)", "registration.browser_timeout_seconds", "90"),
                    Text("registration_browser_locale", "Browser locale", "registration.browser_locale", "en-US"),
                    Text("registration_browser_timezone", "Browser timezone", "registration.browser_timezone", "America/New_York")),
                Section("RoxyBrowser",
                    Text("roxy_api_base", "Local API URL", "registration.drivers.roxy.api_base", "http://127.0.0.1:50000"),
                    Secret("roxy_api_token", "API Token", "registration.drivers.roxy.api_token"),
                    Text("roxy_workspace_id", "Workspace ID", "registration.drivers.roxy.workspace_id"),
                    Text("roxy_project_id", "Project ID", "registration.drivers.roxy.project_id"),
                    Text("roxy_profile_id", "Pinned profile ID", "registration.drivers.roxy.profile_id"),
                    Boolean("roxy_delete_profile", "Delete temp profile after run", "registration.drivers.roxy.delete_profile_after_run", true),
                    Boolean("roxy_keep_browser_open", "Keep browser open", "registration.drivers.roxy.keep_browser_open", false)),
                Section("CloakBrowser",
                    Boolean("cloak_humanize", "Human-like behavior", "registration.drivers.cloak.humanize", true),
                    Boolean("cloak_geoip", "Match environment by exit IP", "registration.drivers.cloak.geoip", true),
                    Boolean("cloak_use_proxy", "Use registration proxy", "registration.drivers.cloak.use_proxy", true),
                    Secret("cloak_license_key", "License Key", "registration.drivers.cloak.license_key"),
                    Text("cloak_fingerprint_seed", "Fingerprint seed", "registration.drivers.cloak.fingerprint_seed"),
                    Text("cloak_user_data_dir", "Persistent user data dir", "registration.drivers.cloak.user_data_dir"),
                    Boolean("cloak_keep_browser_open", "Keep browser open", "registration.drivers.cloak.keep_browser_open", false)),
                Section("Camoufox",
                    Boolean("camoufox_humanize", "Human-like behavior", "registration.drivers.camoufox.humanize", true),
                    Boolean("camoufox_geoip", "Match environment by exit IP", "registration.drivers.camoufox.geoip", true),
                    Boolean("camoufox_use_proxy", "Use registration proxy", "registration.drivers.camoufox.use_proxy", true),
                    Integer("camoufox_max_width", "Max screen width", "registration.drivers.camoufox.max_width", "1280"),
                    Integer("camoufox_max_height", "Max screen height", "registration.drivers.camoufox.max_height", "900"),
                    Text("camoufox_locale", "Browser locale", "registration.drivers.camoufox.locale"),
                    Text("camoufox_timezone", "Browser timezone", "registration.drivers.camoufox.timezone"),
                    Text("camoufox_user_data_dir", "Persistent user data dir", "registration.drivers.camoufox.user_data_dir"),
                    Boolean("camoufox_keep_browser_open", "Keep browser open", "registration.drivers.camoufox.keep_browser_open", false)),
                Section("SMS provider",
                    Options("phone_provider", "Provider", "phone_reuse.source", DefaultPhoneProvider,
                        "smsbower", "herosms", "grizzly", "nexsms"),
                    Secret("phone_provider_api_key", "API Key", "phone_reuse.{provider}.api_key"),
                    Text("phone_provider_endpoint", "API URL (blank = built-in default)", "phone_reuse.{provider}.endpoint"),
                    Integer("phone_provider_sms_timeout", "SMS wait (s)", "phone_reuse.{provider}.sms_timeout", "120"),
                    Integer("phone_provider_sms_poll_interval", "SMS polling interval (s)", "phone_reuse.{provider}.sms_poll_interval", "5"),
                    Integer("phone_max_reuse_count", "Reuse count", "phone_reuse.max_reuse_count"),
                    Integer("phone_send_cooldown_seconds", "Send cooldown (s)", "phone_reuse.send_cooldown_seconds"),
                    Integer("phone_send_retry_attempts", "Send retry attempts", "phone_reuse.send_retry_attempts"),
                    Integer("phone_send_retry_delay_seconds", "Send retry delay (s)", "phone_reuse.send_retry_delay_seconds"),
                    Text("phone_state_file", "State file", "phone_reuse.state_file")),
                Section("Codex OAuth",
                    Integer("codex_registration_timeout", "OAuth timeout (s)", "codex_oauth.registration_timeout"),
                    Boolean("codex_allow_passwordless_takeover", "Allow email OTP fallback", "codex_oauth.allow_passwordless_takeover", false),
                    Boolean("codex_auto_phone_verification", "Automatic phone verification", "codex_oauth.auto_phone_verification", false),
                    Boolean("codex_require_registration_refresh_token", "Require RT for registration", "codex_oauth.require_registration_refresh_token", true),
                    Boolean("codex_require_registration_phone_verification", "Require phone for registration", "codex_oauth.require_registration_phone_verification", true)),
                Section("AT stability",
                    Integer("registration_at_stability_probe_count", "AT probe count", "registration.at_stability_probe_count", "2"),
                    Integer("registration_at_stability_probe_delay", "Probe interval (s)", "registration.at_stability_probe_delay_seconds", "10"),
                    Integer("registration_at_probe_timeout", "Per-probe timeout (s)", "registration.at_probe_timeout_seconds", "30")),
                Section("Stage concurrency",
                    Integer("registration_auth_concurrency", "Auth flow concurrency", "registration.stage_concurrency.auth", "1"),
                    Integer("registration_network_concurrency", "Registration network concurrency", "registration.stage_concurrency.network", "4"),
                    Integer("registration_at_probe_concurrency", "AT probe concurrency", "registration.stage_concurrency.at_probe", "4")),
                Section("Pulse scheduling",
                    Boolean("pulse_enabled", "Enable wave scheduling", "registration.pulse.enabled", false),
                    Boolean("pulse_canary_enabled", "Canary probe on first wave and after blocks", "registration.pulse.canary_enabled", true),
                    Integer("pulse_wave_size", "Accounts per wave", "registration.pulse.wave_size", "4"),
                    Integer("pulse_wave_delay", "Wave interval (s)", "registration.pulse.wave_delay_seconds", "5"),
                    Integer("pulse_ban_threshold", "OTP ban threshold", "registration.pulse.ban_threshold", "2"),
                    Integer("pulse_ban_pause", "Ban pause (s)", "registration.pulse.ban_pause_seconds", "60"),
                    Integer("pulse_max_waves", "Max waves (0 = unlimited)", "registration.pulse.max_waves", "0")),
                Section("Cross-batch retry",
                    Integer("registration_cross_batch_cooldown", "Cross-batch cooldown (s)", "registration.retry_policy.cross_batch_cooldown_seconds", "1800"),
                    Integer("registration_otp_pending_quarantine", "OTP pending quarantine threshold", "registration.retry_policy.otp_pending_quarantine_threshold", "2")),
                Section("Browser process pool",
                    Boolean("browser_pool_enabled", "Enable process pool", "registration.browser_process_pool.enabled", false),
                    Integer("browser_pool_max_concurrent", "Max concurrent browsers", "registration.browser_process_pool.max_concurrent", "4"),
                    Integer("browser_pool_max_uses", "Max reuses per process", "registration.browser_process_pool.max_uses_per_process", "10"),
                    Boolean("browser_pool_recycle_on_error", "Recycle process on error", "registration.browser_process_pool.recycle_on_error", true)),
                Section("Sentinel",
                    Text("sentinel_version", "Sentinel version", "email_registration.sentinel_version"),
                    Integer("sentinel_max_concurrency", "Extraction concurrency", "email_registration.sentinel_max_concurrency", "2"),
                    Integer("sentinel_prewarm_window", "One-to-one prewarm window", "email_registration.sentinel_prewarm_window", "4"),
                    Integer("sentinel_circuit_failures", "Circuit-breaker failure count", "email_registration.sentinel_circuit_failures", "3"),
                    Integer("sentinel_circuit_cooldown", "Circuit-breaker cooldown (s)", "email_registration.sentinel_circuit_cooldown_seconds", "60"))),

            Category("Import & Accounts",
                Section("CPA",
                    Text("cpa_api_url", "CPA URL", "cpa_mode.api_url"),
                    Secret("cpa_api_token", "CPA Token", "cpa_mode.api_token")),
                Section("SUB2API",
                    Text("sub2api_url", "API URL", "sub2api.api_url"),
                    Secret("sub2api_token", "API Token", "sub2api.api_token"),
                    Text("sub2api_email", "Login email", "sub2api.email"),
                    Secret("sub2api_password", "Login password", "sub2api.password"),
                    Text("sub2api_group", "Target group", "sub2api.group_name"),
                    Text("sub2api_group_ids", "Group IDs", "sub2api.group_ids"),
                    Text("sub2api_proxy", "Remote proxy", "sub2api.proxy_name"),
                    Text("sub2api_proxy_id", "Proxy ID", "sub2api.proxy_id"),
                    Integer("sub2api_priority", "Priority", "sub2api.priority"),
                    Integer("sub2api_concurrency", "Account concurrency", "sub2api.concurrency"),
                    Options("sub2api_auth_mode", "Credential mode", "sub2api.auth_mode", "auto", "auto", "oauth", "agent_identity"),
                    Boolean("sub2api_verify_after_import", "Connectivity test after import", "sub2api.verify_after_import", true))),

            Category("Network & Payment",
                Section("Network basics",
                    Text("registration_proxy", "Registration proxy (primary; host:port:user:password / http / socks5 / socks5h)", "", "http://127.0.0.1:7897"),
                    Multiline("registration_proxy_pool", "Registration proxy pool (host:port:user:password / http / socks5 / socks5h)", ""),
                    Text("mailbox_proxy", "Mailbox inbox proxy", "", "http://127.0.0.1:7897"),
                    Multiline("mailbox_proxy_pool", "Mailbox inbox proxy pool (auto-failover on network errors)", "")),
                Section("Protocol management",
                    Text("protocol_enabled_methods", "Enabled methods", "", "paypal,gopay,gcash,grabpay,upi,ideal,pix,kakao,blik,twint,direct_card,momo"),
                    Text("protocol_reference_root", "Extractor directory", "protocol_payments.reference_root", "services/protocol-payment"),
                    Text("protocol_state_file", "State file", "protocol_payments.state_file", "runtime/payment_link_runs.jsonl"),
                    Integer("protocol_timeout_seconds", "Protocol timeout (s)", "protocol_payments.timeout_seconds", "900")),
                Section("Production batch payment",
                    Integer("protocol_batch_momo_workers", "MoMo workers", "protocol_payments.batch.method_workers.momo", "2"),
                    Integer("protocol_batch_kakao_workers", "Kakao workers", "protocol_payments.batch.method_workers.kakao", "2"),
                    Integer("protocol_batch_gopay_workers", "GoPay workers", "protocol_payments.batch.method_workers.gopay", "2"),
                    Integer("protocol_batch_gcash_workers", "GCash workers", "protocol_payments.batch.method_workers.gcash", "2"),
                    Integer("protocol_batch_grabpay_workers", "GrabPay workers", "protocol_payments.batch.method_workers.grabpay", "2"),
                    Boolean("protocol_batch_pause_on_canary_failure", "Pause on canary failure", "protocol_payments.batch.pause_on_canary_failure", true),
                    Integer("protocol_batch_canary_pause_seconds", "Pause duration (s)", "protocol_payments.batch.canary_pause_seconds", "21600"),
                    Multiline("protocol_payment_matrix", "Region eligibility matrix JSON", "")),
                Section("PayPal",
                    Multiline("paypal_proxy", "PayPal proxy pool", ""),
                    Options("paypal_billing_region", "Order generation region", "", "DE", "JP", "US", "AU", "DE", "FR", "GB", "IN", "BR"),
                    Options("paypal_link_generation_type", "PayPal direct-link mode", "paypal.link_generation_type", "hosted_long_url", "hosted_long_url", "paypal_direct", "paypal_direct_zero_due"))),

            Category("Data & Files",
                Section("Runtime",
                    Text("python_path", "Python interpreter path", "runtime.python_path", "python")),
                Section("Local storage",
                    Text("output_directory", "Session directory", "output.directory"),
                    Text("sqlite_path", "SQLite path", "storage.sqlite_path")))
        };

        public static IEnumerable<SettingDefinition> AllFields => Categories.SelectMany(category => category.Sections).SelectMany(section => section.Fields);
    }
}
