package com.betterdemo.remote

import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class RemoteEndpointTests {
    @Test
    fun normalizesAValidPrivateIpv4EndpointAndFingerprint() {
        val fingerprint = "ab:".repeat(31) + "cd"

        val endpoint = RemoteEndpoint("192.168.1.20", 47829, fingerprint).validate()

        assertEquals("192.168.1.20", endpoint.host)
        assertEquals(47829, endpoint.port)
        assertEquals("AB".repeat(31) + "CD", endpoint.fingerprint)
    }

    @Test
    fun acceptsTheThreeRfc1918AddressRanges() {
        listOf("10.2.3.4", "172.16.0.1", "172.31.255.254", "192.168.0.10")
            .forEach { address ->
                RemoteEndpoint(address, 47829, "A".repeat(64)).validate()
            }
    }

    @Test
    fun rejectsNonPrivateOrNonCanonicalIpv4Addresses() {
        listOf(
            "8.8.8.8",
            "172.15.0.1",
            "172.32.0.1",
            "127.0.0.1",
            "169.254.1.1",
            "192.168.1.256",
            "192.168.01.20",
            "192.168.1.20.",
            "desktop.local",
        ).forEach { address ->
            assertThrows(IllegalArgumentException::class.java) {
                RemoteEndpoint(address, 47829, "A".repeat(64)).validate()
            }
        }
    }

    @Test
    fun rejectsInvalidPortsAndMalformedFingerprints() {
        assertThrows(IllegalArgumentException::class.java) {
            RemoteEndpoint("192.168.1.20", 0, "A".repeat(64)).validate()
        }
        assertThrows(IllegalArgumentException::class.java) {
            RemoteEndpoint("192.168.1.20", 65536, "A".repeat(64)).validate()
        }
        assertThrows(IllegalArgumentException::class.java) {
            RemoteEndpoint("192.168.1.20", 47829, "Z".repeat(64)).validate()
        }
        assertThrows(IllegalArgumentException::class.java) {
            RemoteEndpoint("192.168.1.20", 47829, "AB:CD").validate()
        }
    }

    @Test
    fun parsesDesktopPairingQrIntoValidatedEndpointAndOneTimeCode() {
        val fingerprint = "AB".repeat(32)
        val payload = PairingQrPayload.parse(
            "betterdemo://pair?host=192.168.1.20&port=47829&code=004219&fingerprint=${fingerprint.chunked(2).joinToString(":")}",
        )

        assertEquals("192.168.1.20", payload.endpoint.host)
        assertEquals(47829, payload.endpoint.port)
        assertEquals(fingerprint, payload.endpoint.fingerprint)
        assertEquals("004219", payload.oneTimeCode)
    }

    @Test
    fun rejectsMalformedOrNonBetterDemoPairingQr() {
        listOf(
            "https://example.com/pair?host=192.168.1.20&port=47829&code=004219&fingerprint=${"A".repeat(64)}",
            "betterdemo://pair?host=8.8.8.8&port=47829&code=004219&fingerprint=${"A".repeat(64)}",
            "betterdemo://pair?host=192.168.1.20&port=47829&code=42&fingerprint=${"A".repeat(64)}",
        ).forEach { raw ->
            assertThrows(IllegalArgumentException::class.java) { PairingQrPayload.parse(raw) }
        }
    }
}
