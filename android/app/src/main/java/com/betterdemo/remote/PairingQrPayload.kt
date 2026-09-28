package com.betterdemo.remote

import java.net.URI
import java.net.URLDecoder
import java.nio.charset.StandardCharsets

data class PairingQrPayload(
    val endpoint: RemoteEndpoint,
    val oneTimeCode: String,
) {
    companion object {
        fun parse(rawValue: String): PairingQrPayload {
            val uri = try {
                URI(rawValue.trim())
            } catch (_: Exception) {
                throw IllegalArgumentException("This is not a valid BetterDemo pairing QR.")
            }
            require(uri.scheme.equals("betterdemo", ignoreCase = true) && uri.host.equals("pair", ignoreCase = true)) {
                "This QR code does not belong to BetterDemo."
            }

            val parameters = uri.rawQuery.orEmpty().split('&')
                .filter(String::isNotBlank)
                .associate { part ->
                    val separator = part.indexOf('=')
                    val key = if (separator < 0) part else part.substring(0, separator)
                    val value = if (separator < 0) "" else part.substring(separator + 1)
                    decode(key) to decode(value)
                }
            fun value(name: String): String = parameters[name].orEmpty()

            val code = value("code")
            require(code.matches(Regex("[0-9]{6}"))) { "The desktop QR has no valid one-time code. Refresh it and scan again." }
            val port = value("port").toIntOrNull()
                ?: throw IllegalArgumentException("The desktop QR has no valid network port.")
            val endpoint = RemoteEndpoint(value("host"), port, value("fingerprint")).validate()
            return PairingQrPayload(endpoint, code)
        }

        @Suppress("DEPRECATION")
        private fun decode(value: String): String = URLDecoder.decode(value, StandardCharsets.UTF_8.name())
    }
}
