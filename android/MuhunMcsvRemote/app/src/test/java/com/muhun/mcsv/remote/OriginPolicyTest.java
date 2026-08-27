package com.muhun.mcsv.remote;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import org.junit.Test;

public final class OriginPolicyTest {
    @Test
    public void configuredOriginRequiresExactHttpsDnsOrigin() {
        assertEquals(
                "https://machine.tail123.ts.net",
                OriginPolicy.normalizeConfiguredOrigin(" HTTPS://Machine.Tail123.TS.NET/ ").orElseThrow());
        assertTrue(OriginPolicy.normalizeConfiguredOrigin("http://machine.ts.net").isEmpty());
        assertTrue(OriginPolicy.normalizeConfiguredOrigin("https://user@machine.ts.net").isEmpty());
        assertTrue(OriginPolicy.normalizeConfiguredOrigin("https://machine.ts.net/panel").isEmpty());
        assertTrue(OriginPolicy.normalizeConfiguredOrigin("https://machine.ts.net?token=x").isEmpty());
        assertTrue(OriginPolicy.normalizeConfiguredOrigin("https://machine.ts.net#panel").isEmpty());
        assertTrue(OriginPolicy.normalizeConfiguredOrigin("https://machine.ts.net:444").isEmpty());
        assertTrue(OriginPolicy.normalizeConfiguredOrigin("https://127.0.0.1").isEmpty());
        assertTrue(OriginPolicy.normalizeConfiguredOrigin("https://[::1]").isEmpty());
    }

    @Test
    public void navigationAllowsOnlyTheConfiguredOrigin() {
        String approved = "https://machine.tail123.ts.net";
        assertTrue(OriginPolicy.isAllowedNavigation(approved, approved + "/api/v1/servers?after=1"));
        assertTrue(OriginPolicy.isAllowedNavigation(approved, "https://machine.tail123.ts.net:443/"));
        assertFalse(OriginPolicy.isAllowedNavigation(approved, "http://machine.tail123.ts.net/"));
        assertFalse(OriginPolicy.isAllowedNavigation(approved, "https://machine.tail123.ts.net.evil.example/"));
        assertFalse(OriginPolicy.isAllowedNavigation(approved, "https://machine.tail123.ts.net@evil.example/"));
        assertFalse(OriginPolicy.isAllowedNavigation(approved, "https://machine.tail123.ts.net:444/"));
        assertFalse(OriginPolicy.isAllowedNavigation(approved, "javascript:alert(1)"));
    }

    @Test
    public void originNormalizationIsBoundedAndRejectsAmbiguousNames() {
        assertTrue(OriginPolicy.normalizeConfiguredOrigin(null).isEmpty());
        assertTrue(OriginPolicy.normalizeConfiguredOrigin("https://localhost").isEmpty());
        assertTrue(OriginPolicy.normalizeConfiguredOrigin("https://-bad.example").isEmpty());
        assertTrue(OriginPolicy.normalizeConfiguredOrigin("https://bad-.example").isEmpty());
        assertTrue(OriginPolicy.normalizeConfiguredOrigin("https://example..com").isEmpty());
        assertTrue(OriginPolicy.normalizeConfiguredOrigin("https://example.com.").isEmpty());
        assertTrue(OriginPolicy.normalizeConfiguredOrigin("https://" + "a".repeat(600) + ".com").isEmpty());
    }
}
