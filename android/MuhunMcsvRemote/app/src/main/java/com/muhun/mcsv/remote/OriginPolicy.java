package com.muhun.mcsv.remote;

import java.net.IDN;
import java.net.URI;
import java.net.URISyntaxException;
import java.util.Locale;
import java.util.Optional;

final class OriginPolicy {
    private OriginPolicy() {
    }

    static Optional<String> normalizeConfiguredOrigin(String input) {
        if (input == null) {
            return Optional.empty();
        }

        String candidate = input.trim();
        if (candidate.length() < 12 || candidate.length() > 512) {
            return Optional.empty();
        }

        try {
            URI uri = new URI(candidate);
            if (!"https".equalsIgnoreCase(uri.getScheme())
                    || uri.getRawUserInfo() != null
                    || uri.getRawQuery() != null
                    || uri.getRawFragment() != null
                    || (uri.getPort() != -1 && uri.getPort() != 443)
                    || (uri.getRawPath() != null
                        && !uri.getRawPath().isEmpty()
                        && !"/".equals(uri.getRawPath()))) {
                return Optional.empty();
            }

            String host = normalizeDnsHost(uri.getHost());
            if (host == null) {
                return Optional.empty();
            }

            return Optional.of(new URI("https", null, host, -1, null, null, null).toASCIIString());
        } catch (IllegalArgumentException | URISyntaxException ignored) {
            return Optional.empty();
        }
    }

    static boolean isAllowedNavigation(String configuredOrigin, String candidateUrl) {
        Optional<String> origin = normalizeConfiguredOrigin(configuredOrigin);
        if (origin.isEmpty() || candidateUrl == null || candidateUrl.length() > 4096) {
            return false;
        }

        try {
            URI candidate = new URI(candidateUrl);
            if (!"https".equalsIgnoreCase(candidate.getScheme())
                    || candidate.getRawUserInfo() != null
                    || (candidate.getPort() != -1 && candidate.getPort() != 443)) {
                return false;
            }

            String host = normalizeDnsHost(candidate.getHost());
            URI approved = new URI(origin.get());
            return host != null && host.equals(approved.getHost());
        } catch (IllegalArgumentException | URISyntaxException ignored) {
            return false;
        }
    }

    private static String normalizeDnsHost(String rawHost) {
        if (rawHost == null || rawHost.isBlank() || rawHost.length() > 253) {
            return null;
        }

        String ascii = IDN.toASCII(rawHost, IDN.USE_STD3_ASCII_RULES).toLowerCase(Locale.ROOT);
        if (ascii.length() > 253 || ascii.endsWith(".") || !ascii.contains(".")) {
            return null;
        }

        boolean numericOnly = true;
        for (String label : ascii.split("\\.", -1)) {
            if (label.isEmpty() || label.length() > 63
                    || label.startsWith("-") || label.endsWith("-")) {
                return null;
            }

            for (int index = 0; index < label.length(); index++) {
                char character = label.charAt(index);
                if (!((character >= 'a' && character <= 'z')
                        || (character >= '0' && character <= '9')
                        || character == '-')) {
                    return null;
                }
                numericOnly &= (character >= '0' && character <= '9');
            }
        }

        return numericOnly ? null : ascii;
    }
}
