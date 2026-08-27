package com.muhun.mcsv.remote;

import android.annotation.SuppressLint;
import android.app.Activity;
import android.app.AlertDialog;
import android.graphics.Color;
import android.graphics.Typeface;
import android.net.Uri;
import android.net.http.SslError;
import android.os.Build;
import android.os.Bundle;
import android.text.InputType;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.view.Window;
import android.view.inputmethod.EditorInfo;
import android.webkit.CookieManager;
import android.webkit.GeolocationPermissions;
import android.webkit.PermissionRequest;
import android.webkit.RenderProcessGoneDetail;
import android.webkit.SafeBrowsingResponse;
import android.webkit.SslErrorHandler;
import android.webkit.ValueCallback;
import android.webkit.WebChromeClient;
import android.webkit.WebResourceError;
import android.webkit.WebResourceRequest;
import android.webkit.WebResourceResponse;
import android.webkit.WebSettings;
import android.webkit.WebStorage;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.webkit.WebViewDatabase;
import android.widget.Button;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.ProgressBar;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import java.io.ByteArrayInputStream;
import java.nio.charset.StandardCharsets;
import java.util.Collections;
import java.util.Locale;
import java.util.Optional;

/**
 * Native shell for the Service-owned remote panel. This is deliberately not a general browser:
 * one normalized HTTPS origin is the complete network boundary, no Java object is exposed to
 * JavaScript, and every certificate error terminates the navigation.
 */
public final class MainActivity extends Activity {
    private static final String PREFERENCES_NAME = "trusted_remote_origin_v1";
    private static final String ORIGIN_KEY = "origin";
    private static final int BACKGROUND_COLOR = Color.rgb(11, 14, 19);
    private static final int PANEL_COLOR = Color.rgb(21, 26, 34);
    private static final int ACCENT_COLOR = Color.rgb(53, 201, 121);
    private static final int TEXT_COLOR = Color.rgb(242, 245, 247);
    private static final int MUTED_COLOR = Color.rgb(170, 180, 192);

    private FrameLayout root;
    private WebView webView;
    private ProgressBar progressBar;
    private LinearLayout errorPanel;
    private TextView errorText;
    private String configuredOrigin;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        Window window = getWindow();
        window.setNavigationBarColor(BACKGROUND_COLOR);
        window.setStatusBarColor(BACKGROUND_COLOR);

        root = new FrameLayout(this);
        root.setBackgroundColor(BACKGROUND_COLOR);
        root.setFitsSystemWindows(true);
        setContentView(root);

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            Api33BackNavigation.register(this, this::navigateBack);
        }

        String saved = getSharedPreferences(PREFERENCES_NAME, MODE_PRIVATE)
                .getString(ORIGIN_KEY, null);
        Optional<String> normalized = OriginPolicy.normalizeConfiguredOrigin(saved);
        if (normalized.isPresent() && normalized.get().equals(saved)) {
            showBrowser(normalized.get());
        } else {
            getSharedPreferences(PREFERENCES_NAME, MODE_PRIVATE).edit().remove(ORIGIN_KEY).apply();
            showSetup(saved == null ? "" : saved);
        }
    }

    private void showSetup(String initialValue) {
        destroyWebView();
        configuredOrigin = null;
        root.removeAllViews();

        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        scroll.setBackgroundColor(BACKGROUND_COLOR);
        scroll.setLayoutParams(new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.MATCH_PARENT));

        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setGravity(Gravity.CENTER_VERTICAL);
        page.setPadding(dp(24), dp(32), dp(24), dp(32));
        scroll.addView(page, new ScrollView.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT));

        TextView brand = text(getString(R.string.app_name), 14, ACCENT_COLOR);
        brand.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        page.addView(brand, margins(matchWidthWrapHeight(), 0, 0, 0, 10));

        TextView title = text(getString(R.string.setup_title), 28, TEXT_COLOR);
        title.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        page.addView(title, margins(matchWidthWrapHeight(), 0, 0, 0, 12));

        TextView description = text(getString(R.string.setup_description), 15, MUTED_COLOR);
        description.setLineSpacing(0, 1.18f);
        page.addView(description, margins(matchWidthWrapHeight(), 0, 0, 0, 24));

        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setPadding(dp(18), dp(18), dp(18), dp(18));
        card.setBackground(roundedPanel(PANEL_COLOR, 14));
        page.addView(card, matchWidthWrapHeight());

        TextView label = text(getString(R.string.server_address), 14, TEXT_COLOR);
        label.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        card.addView(label, margins(matchWidthWrapHeight(), 0, 0, 0, 8));

        EditText input = new EditText(this);
        input.setSingleLine(true);
        input.setText(initialValue);
        input.setHint(R.string.setup_hint);
        input.setHintTextColor(MUTED_COLOR);
        input.setTextColor(TEXT_COLOR);
        input.setTextSize(15);
        input.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_URI);
        input.setImeOptions(EditorInfo.IME_ACTION_GO);
        input.setPadding(dp(14), dp(12), dp(14), dp(12));
        input.setBackground(roundedPanel(Color.rgb(8, 11, 16), 9));
        card.addView(input, margins(matchWidthWrapHeight(), 0, 0, 0, 12));

        TextView validation = text("", 13, Color.rgb(255, 122, 122));
        validation.setVisibility(View.GONE);
        card.addView(validation, margins(matchWidthWrapHeight(), 0, 0, 0, 10));

        Button connect = button(getString(R.string.connect), true);
        card.addView(connect, matchWidthHeight(dp(52)));
        View.OnClickListener connectAction = ignored -> {
            Optional<String> normalized = OriginPolicy.normalizeConfiguredOrigin(
                    input.getText().toString());
            if (normalized.isEmpty()) {
                validation.setText(R.string.invalid_origin);
                validation.setVisibility(View.VISIBLE);
                input.requestFocus();
                return;
            }

            if (!getSharedPreferences(PREFERENCES_NAME, MODE_PRIVATE)
                    .edit()
                    .putString(ORIGIN_KEY, normalized.get())
                    .commit()) {
                validation.setText(R.string.save_failed);
                validation.setVisibility(View.VISIBLE);
                return;
            }

            validation.setVisibility(View.GONE);
            showBrowser(normalized.get());
        };
        connect.setOnClickListener(connectAction);
        input.setOnEditorActionListener((ignored, actionId, event) -> {
            if (actionId == EditorInfo.IME_ACTION_GO) {
                connectAction.onClick(input);
                return true;
            }
            return false;
        });

        TextView note = text(getString(R.string.security_note), 13, MUTED_COLOR);
        note.setLineSpacing(0, 1.15f);
        page.addView(note, margins(matchWidthWrapHeight(), 0, 18, 0, 0));
        root.addView(scroll);
    }

    @SuppressLint("SetJavaScriptEnabled") // The exact-origin MCSV SPA needs JS; no JS bridge exists.
    private void showBrowser(String origin) {
        destroyWebView();
        configuredOrigin = origin;
        root.removeAllViews();

        LinearLayout shell = new LinearLayout(this);
        shell.setOrientation(LinearLayout.VERTICAL);
        shell.setBackgroundColor(BACKGROUND_COLOR);
        root.addView(shell, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.MATCH_PARENT));

        LinearLayout toolbar = new LinearLayout(this);
        toolbar.setOrientation(LinearLayout.HORIZONTAL);
        toolbar.setGravity(Gravity.CENTER_VERTICAL);
        toolbar.setPadding(dp(16), dp(8), dp(10), dp(8));
        toolbar.setBackgroundColor(PANEL_COLOR);
        shell.addView(toolbar, matchWidthHeight(dp(58)));

        LinearLayout identity = new LinearLayout(this);
        identity.setOrientation(LinearLayout.VERTICAL);
        TextView title = text(getString(R.string.app_name), 17, TEXT_COLOR);
        title.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        identity.addView(title, matchWidthWrapHeight());
        TextView host = text(Uri.parse(origin).getHost(), 11, MUTED_COLOR);
        host.setSingleLine(true);
        identity.addView(host, matchWidthWrapHeight());
        toolbar.addView(identity, new LinearLayout.LayoutParams(
                0,
                ViewGroup.LayoutParams.WRAP_CONTENT,
                1));

        Button change = button(getString(R.string.change_server), false);
        change.setContentDescription(getString(R.string.change_server));
        change.setOnClickListener(ignored -> showClearConfirmation());
        toolbar.addView(change, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT,
                dp(42)));

        progressBar = new ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal);
        progressBar.setMax(100);
        progressBar.setProgressTintList(android.content.res.ColorStateList.valueOf(ACCENT_COLOR));
        shell.addView(progressBar, matchWidthHeight(dp(2)));

        FrameLayout browserFrame = new FrameLayout(this);
        shell.addView(browserFrame, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                0,
                1));

        WebView.setWebContentsDebuggingEnabled(false);
        webView = new WebView(this);
        webView.setBackgroundColor(BACKGROUND_COLOR);
        webView.setOverScrollMode(View.OVER_SCROLL_NEVER);
        WebSettings settings = webView.getSettings();
        settings.setJavaScriptEnabled(true);
        settings.setDomStorageEnabled(true);
        settings.setDatabaseEnabled(false);
        settings.setAllowFileAccess(false);
        settings.setAllowContentAccess(false);
        settings.setAllowFileAccessFromFileURLs(false);
        settings.setAllowUniversalAccessFromFileURLs(false);
        settings.setMixedContentMode(WebSettings.MIXED_CONTENT_NEVER_ALLOW);
        settings.setSafeBrowsingEnabled(true);
        settings.setGeolocationEnabled(false);
        settings.setSupportMultipleWindows(false);
        settings.setJavaScriptCanOpenWindowsAutomatically(false);
        settings.setMediaPlaybackRequiresUserGesture(true);
        settings.setSaveFormData(false);
        settings.setBuiltInZoomControls(false);
        settings.setDisplayZoomControls(false);
        settings.setCacheMode(WebSettings.LOAD_DEFAULT);

        CookieManager cookies = CookieManager.getInstance();
        cookies.setAcceptCookie(true);
        cookies.setAcceptThirdPartyCookies(webView, false);

        webView.setWebViewClient(Build.VERSION.SDK_INT >= Build.VERSION_CODES.O_MR1
                ? new Api27TrustedWebViewClient(origin)
                : new TrustedWebViewClient(origin));
        webView.setWebChromeClient(new LockedWebChromeClient());
        webView.setDownloadListener((url, userAgent, contentDisposition, mimeType, contentLength) ->
                Toast.makeText(this, R.string.download_blocked, Toast.LENGTH_LONG).show());
        browserFrame.addView(webView, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.MATCH_PARENT));

        errorPanel = new LinearLayout(this);
        errorPanel.setOrientation(LinearLayout.VERTICAL);
        errorPanel.setGravity(Gravity.CENTER);
        errorPanel.setPadding(dp(28), dp(28), dp(28), dp(28));
        errorPanel.setBackgroundColor(BACKGROUND_COLOR);
        errorPanel.setVisibility(View.GONE);
        TextView errorTitle = text(getString(R.string.connection_title), 22, TEXT_COLOR);
        errorTitle.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        errorPanel.addView(errorTitle, margins(matchWidthWrapHeight(), 0, 0, 0, 12));
        errorText = text(getString(R.string.connection_failed), 15, MUTED_COLOR);
        errorText.setGravity(Gravity.CENTER);
        errorText.setLineSpacing(0, 1.15f);
        errorPanel.addView(errorText, margins(matchWidthWrapHeight(), 0, 0, 0, 20));
        Button retry = button(getString(R.string.retry), true);
        retry.setOnClickListener(ignored -> loadApprovedOrigin());
        errorPanel.addView(retry, new LinearLayout.LayoutParams(dp(210), dp(52)));
        browserFrame.addView(errorPanel, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.MATCH_PARENT));

        loadApprovedOrigin();
    }

    private void loadApprovedOrigin() {
        if (webView == null || configuredOrigin == null) {
            return;
        }
        errorPanel.setVisibility(View.GONE);
        webView.setVisibility(View.VISIBLE);
        progressBar.setVisibility(View.VISIBLE);
        progressBar.setProgress(0);
        webView.loadUrl(configuredOrigin + "/");
    }

    private void showClearConfirmation() {
        new AlertDialog.Builder(this)
                .setTitle(R.string.change_server_title)
                .setMessage(R.string.change_server_message)
                .setNegativeButton(R.string.cancel, null)
                .setPositiveButton(R.string.clear, (dialog, which) -> clearConnection())
                .show();
    }

    private void clearConnection() {
        if (webView != null) {
            webView.stopLoading();
            webView.clearHistory();
            webView.clearCache(true);
            webView.clearFormData();
        }
        WebStorage.getInstance().deleteAllData();
        WebViewDatabase.getInstance(this).clearHttpAuthUsernamePassword();
        getSharedPreferences(PREFERENCES_NAME, MODE_PRIVATE).edit().clear().apply();
        CookieManager.getInstance().removeAllCookies(ignored -> runOnUiThread(() -> {
            CookieManager.getInstance().flush();
            showSetup("");
        }));
    }

    private void showConnectionError(int messageResource) {
        runOnUiThread(() -> {
            if (webView == null || errorPanel == null) {
                return;
            }
            webView.stopLoading();
            webView.setVisibility(View.GONE);
            progressBar.setVisibility(View.GONE);
            errorText.setText(messageResource);
            errorPanel.setVisibility(View.VISIBLE);
        });
    }

    private void navigateBack() {
        if (webView != null && webView.getVisibility() == View.VISIBLE && webView.canGoBack()) {
            webView.goBack();
        } else {
            finish();
        }
    }

    @SuppressLint("GestureBackNavigation") // API 33+ uses OnBackInvokedDispatcher above.
    @SuppressWarnings("deprecation")
    @Override
    public void onBackPressed() {
        navigateBack();
    }

    @Override
    protected void onPause() {
        CookieManager.getInstance().flush();
        if (webView != null) {
            webView.onPause();
        }
        super.onPause();
    }

    @Override
    protected void onResume() {
        super.onResume();
        if (webView != null) {
            webView.onResume();
        }
    }

    @Override
    protected void onDestroy() {
        destroyWebView();
        super.onDestroy();
    }

    private void destroyWebView() {
        WebView current = webView;
        webView = null;
        if (current == null) {
            return;
        }
        current.setWebChromeClient(null);
        current.setWebViewClient(null);
        current.stopLoading();
        current.loadUrl("about:blank");
        current.removeAllViews();
        if (current.getParent() instanceof ViewGroup) {
            ((ViewGroup) current.getParent()).removeView(current);
        }
        current.destroy();
    }

    private TextView text(String value, int sp, int color) {
        TextView view = new TextView(this);
        view.setText(value);
        view.setTextSize(sp);
        view.setTextColor(color);
        return view;
    }

    private Button button(String value, boolean primary) {
        Button button = new Button(this);
        button.setText(value);
        button.setTextSize(14);
        button.setAllCaps(false);
        button.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        button.setTextColor(primary ? Color.rgb(4, 20, 12) : TEXT_COLOR);
        button.setBackground(roundedPanel(primary ? ACCENT_COLOR : Color.rgb(35, 43, 55), 10));
        button.setPadding(dp(14), 0, dp(14), 0);
        return button;
    }

    private android.graphics.drawable.GradientDrawable roundedPanel(int color, int radiusDp) {
        android.graphics.drawable.GradientDrawable drawable =
                new android.graphics.drawable.GradientDrawable();
        drawable.setColor(color);
        drawable.setCornerRadius(dp(radiusDp));
        return drawable;
    }

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }

    private LinearLayout.LayoutParams matchWidthWrapHeight() {
        return new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT);
    }

    private LinearLayout.LayoutParams matchWidthHeight(int height) {
        return new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, height);
    }

    private LinearLayout.LayoutParams margins(
            LinearLayout.LayoutParams parameters,
            int left,
            int top,
            int right,
            int bottom) {
        parameters.setMargins(dp(left), dp(top), dp(right), dp(bottom));
        return parameters;
    }

    private class TrustedWebViewClient extends WebViewClient {
        private final String approvedOrigin;

        private TrustedWebViewClient(String approvedOrigin) {
            this.approvedOrigin = approvedOrigin;
        }

        @Override
        public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) {
            return blockUnapprovedMainNavigation(request.getUrl().toString());
        }

        @SuppressWarnings("deprecation")
        @Override
        public boolean shouldOverrideUrlLoading(WebView view, String url) {
            return blockUnapprovedMainNavigation(url);
        }

        private boolean blockUnapprovedMainNavigation(String url) {
            if (OriginPolicy.isAllowedNavigation(approvedOrigin, url)) {
                return false;
            }
            Toast.makeText(MainActivity.this, R.string.blocked_navigation, Toast.LENGTH_LONG).show();
            return true;
        }

        @Override
        public WebResourceResponse shouldInterceptRequest(WebView view, WebResourceRequest request) {
            Uri uri = request.getUrl();
            String scheme = uri.getScheme();
            if (scheme == null) {
                return blockedResponse();
            }

            String normalizedScheme = scheme.toLowerCase(Locale.ROOT);
            if ("https".equals(normalizedScheme)) {
                return OriginPolicy.isAllowedNavigation(approvedOrigin, uri.toString())
                        ? null
                        : blockedResponse();
            }
            if ("data".equals(normalizedScheme) || "about".equals(normalizedScheme)) {
                return null;
            }
            if ("blob".equals(normalizedScheme)
                    && uri.toString().startsWith("blob:" + approvedOrigin + "/")) {
                return null;
            }
            // HTTP, file/content and every custom scheme are outside the one-origin boundary.
            if (!"https".equals(normalizedScheme)) {
                return blockedResponse();
            }
            return blockedResponse();
        }

        @Override
        public void onPageStarted(WebView view, String url, android.graphics.Bitmap favicon) {
            if (!OriginPolicy.isAllowedNavigation(approvedOrigin, url)) {
                view.stopLoading();
                showConnectionError(R.string.blocked_navigation);
                return;
            }
            progressBar.setVisibility(View.VISIBLE);
        }

        @Override
        public void onPageFinished(WebView view, String url) {
            if (OriginPolicy.isAllowedNavigation(approvedOrigin, url)) {
                progressBar.setVisibility(View.GONE);
                CookieManager.getInstance().flush();
            }
        }

        @Override
        public void onReceivedSslError(WebView view, SslErrorHandler handler, SslError error) {
            handler.cancel();
            view.stopLoading();
            showConnectionError(R.string.tls_failed);
        }

        @Override
        public void onReceivedError(
                WebView view,
                WebResourceRequest request,
                WebResourceError error) {
            if (request.isForMainFrame()) {
                showConnectionError(R.string.connection_failed);
            }
        }

        @Override
        public void onReceivedHttpError(
                WebView view,
                WebResourceRequest request,
                WebResourceResponse errorResponse) {
            if (request.isForMainFrame() && errorResponse.getStatusCode() >= 400) {
                showConnectionError(R.string.connection_failed);
            }
        }

        @Override
        public boolean onRenderProcessGone(WebView view, RenderProcessGoneDetail detail) {
            if (view == webView) {
                String previousOrigin = configuredOrigin;
                webView = null;
                view.setWebChromeClient(null);
                view.setWebViewClient(null);
                if (view.getParent() instanceof ViewGroup) {
                    ((ViewGroup) view.getParent()).removeView(view);
                }
                view.destroy();
                showSetup(previousOrigin == null ? "" : previousOrigin);
                Toast.makeText(MainActivity.this, R.string.webview_restarted, Toast.LENGTH_LONG).show();
            }
            return true;
        }

        private WebResourceResponse blockedResponse() {
            return new WebResourceResponse(
                    "text/plain",
                    StandardCharsets.UTF_8.name(),
                    403,
                    "Blocked by exact-origin policy",
                    Collections.singletonMap("Cache-Control", "no-store"),
                    new ByteArrayInputStream(new byte[0]));
        }
    }

    @SuppressLint("NewApi") // Instantiated only after the guarded API 27 check above.
    private final class Api27TrustedWebViewClient extends TrustedWebViewClient {
        private Api27TrustedWebViewClient(String approvedOrigin) {
            super(approvedOrigin);
        }

        @Override
        public void onSafeBrowsingHit(
                WebView view,
                WebResourceRequest request,
                int threatType,
                SafeBrowsingResponse callback) {
            callback.backToSafety(true);
            showConnectionError(R.string.security_blocked);
        }
    }

    private final class LockedWebChromeClient extends WebChromeClient {
        @Override
        public void onProgressChanged(WebView view, int newProgress) {
            if (progressBar != null) {
                progressBar.setProgress(newProgress);
                progressBar.setVisibility(newProgress >= 100 ? View.GONE : View.VISIBLE);
            }
        }

        @Override
        public void onPermissionRequest(PermissionRequest request) {
            request.deny();
        }

        @Override
        public void onGeolocationPermissionsShowPrompt(
                String origin,
                GeolocationPermissions.Callback callback) {
            callback.invoke(origin, false, false);
        }

        @Override
        public boolean onShowFileChooser(
                WebView webView,
                ValueCallback<Uri[]> filePathCallback,
                FileChooserParams fileChooserParams) {
            filePathCallback.onReceiveValue(null);
            return true;
        }

        @Override
        public boolean onCreateWindow(
                WebView view,
                boolean isDialog,
                boolean isUserGesture,
                android.os.Message resultMsg) {
            return false;
        }
    }

    @SuppressLint("NewApi") // Instantiated only after the guarded API 33 check in onCreate.
    private static final class Api33BackNavigation {
        private Api33BackNavigation() {
        }

        static void register(Activity activity, Runnable action) {
            activity.getOnBackInvokedDispatcher().registerOnBackInvokedCallback(
                    android.window.OnBackInvokedDispatcher.PRIORITY_DEFAULT,
                    action::run);
        }
    }
}
