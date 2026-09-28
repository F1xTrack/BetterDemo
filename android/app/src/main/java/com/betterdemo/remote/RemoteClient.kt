package com.betterdemo.remote

import java.io.IOException
import java.net.URL
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import javax.net.ssl.HostnameVerifier
import javax.net.ssl.HttpsURLConnection
import javax.net.ssl.SSLContext
import javax.net.ssl.X509TrustManager
import org.json.JSONObject

data class RemoteEndpoint(val host: String, val port: Int, val fingerprint: String) {
    fun validate(): RemoteEndpoint {
        require(isPrivateIpv4(host)) { "Enter a private IPv4 address such as 192.168.1.20." }
        require(port in 1..65535) { "Port must be between 1 and 65535." }
        val normalizedFingerprint = normalizeFingerprint(fingerprint)
        require(normalizedFingerprint.matches(Regex("[0-9A-F]{64}"))) {
            "Enter the 64-digit SHA-256 fingerprint shown by the desktop."
        }
        return copy(fingerprint = normalizedFingerprint)
    }

    private fun isPrivateIpv4(value: String): Boolean {
        if (!value.matches(Regex("(?:0|[1-9][0-9]{0,2})(?:\\.(?:0|[1-9][0-9]{0,2})){3}"))) return false
        val octets = value.split('.')
        if (octets.size != 4) return false
        val numbers = octets.map { it.toIntOrNull()?.takeIf { part -> part in 0..255 } ?: return false }
        return numbers[0] == 10 ||
            (numbers[0] == 172 && numbers[1] in 16..31) ||
            (numbers[0] == 192 && numbers[1] == 168)
    }

    private fun normalizeFingerprint(value: String): String = value
        .replace(":", "")
        .replace(" ", "")
        .uppercase()
}

data class PairingResult(val token: String, val expiresAt: String)

data class RemoteSnapshot(
    val stateSequence: Long,
    val mode: String,
    val zoom: Float,
    val panX: Float,
    val panY: Float,
    val cameraAngle: Float,
    val cameraScale: Float,
    val cameraMargin: Float,
    val blurEnabled: Boolean,
    val blurRadius: Float,
    val outputVisible: Boolean,
    val layerVisibility: Map<String, Boolean> = emptyMap(),
)

data class RemoteCommandResult(
    val acknowledgement: String,
    val sequence: Long,
    val stateSequence: Long,
    val errorCode: String?,
    val errorMessage: String?,
    val snapshot: RemoteSnapshot,
)

data class RemotePreviewFrame(val sequence: Long, val jpegBytes: ByteArray)

class RemoteHttpException(val statusCode: Int, message: String) : IOException(message)

class RemoteClient {
    fun pair(endpointInput: RemoteEndpoint, code: String): PairingResult {
        val endpoint = endpointInput.validate()
        require(code.matches(Regex("[0-9]{6}"))) { "Pairing code must contain six digits." }
        val response = post(endpoint, "pair", JSONObject().put("code", code))
        val token = response.getString("token")
        if (token.isBlank()) throw IOException("The desktop returned an empty pairing token.")
        return PairingResult(token, response.optString("expiresAt"))
    }

    fun sendCommand(
        session: StoredRemoteSession,
        sequence: Long,
        command: String,
        payload: JSONObject,
        idempotencyKey: String,
    ): RemoteCommandResult {
        require(sequence > 0)
        val endpoint = RemoteEndpoint(session.host, session.port, session.fingerprint).validate()
        val envelope = JSONObject()
            .put("protocolVersion", PROTOCOL_VERSION)
            .put("sequence", sequence)
            .put("command", command)
            .put("token", session.token)
            .put("idempotencyKey", idempotencyKey)
            .put("payload", payload)

        var response: JSONObject? = null
        var lastFailure: IOException? = null
        for (attempt in 0..1) {
            try {
                response = post(endpoint, "command", envelope)
                break
            } catch (failure: RemoteHttpException) {
                throw failure
            } catch (failure: IOException) {
                lastFailure = failure
                if (attempt == 0) Thread.sleep(120)
            }
        }
        val body = response ?: throw (lastFailure ?: IOException("The command did not receive a response."))

        val ack = body.getJSONObject("ack")
        val snapshot = body.getJSONObject("snapshot")
        val cameraCorner = snapshot.getJSONObject("cameraCorner")
        val pan = snapshot.getJSONObject("pan")
        val blur = snapshot.getJSONObject("blur")
        val layerVisibilityObject = snapshot.optJSONObject("layerVisibility")
        val layerVisibility = buildMap {
            if (layerVisibilityObject != null) {
                val keys = layerVisibilityObject.keys()
                while (keys.hasNext()) {
                    val layerId = keys.next()
                    put(layerId, layerVisibilityObject.optBoolean(layerId, true))
                }
            }
        }
        return RemoteCommandResult(
            acknowledgement = ack.getString("status"),
            sequence = ack.getLong("sequence"),
            stateSequence = ack.getLong("stateSequence"),
            errorCode = if (ack.isNull("errorCode")) null else ack.optString("errorCode").ifBlank { null },
            errorMessage = if (ack.isNull("errorMessage")) null else ack.optString("errorMessage").ifBlank { null },
            snapshot = RemoteSnapshot(
                stateSequence = snapshot.getLong("stateSequence"),
                mode = snapshot.getString("mode"),
                zoom = snapshot.getDouble("zoom").toFloat(),
                panX = pan.getDouble("x").toFloat(),
                panY = pan.getDouble("y").toFloat(),
                cameraAngle = cameraCorner.getDouble("angleDegrees").toFloat(),
                cameraScale = cameraCorner.getDouble("scale").toFloat(),
                cameraMargin = cameraCorner.getDouble("margin").toFloat(),
                blurEnabled = blur.getBoolean("enabled"),
                blurRadius = blur.getDouble("radius").toFloat(),
                outputVisible = snapshot.getBoolean("outputVisible"),
                layerVisibility = layerVisibility,
            ),
        )
    }

    fun fetchPreview(session: StoredRemoteSession): RemotePreviewFrame? {
        val endpoint = RemoteEndpoint(session.host, session.port, session.fingerprint).validate()
        val bytes = JSONObject().put("token", session.token).toString().toByteArray(Charsets.UTF_8)
        val connection = openConnection(endpoint, "preview")
        try {
            connection.requestMethod = "POST"
            connection.doOutput = true
            connection.connectTimeout = CONNECT_TIMEOUT_MS
            connection.readTimeout = 3_000
            connection.setRequestProperty("Content-Type", "application/json; charset=utf-8")
            connection.setRequestProperty("Cache-Control", "no-store")
            connection.setFixedLengthStreamingMode(bytes.size)
            connection.outputStream.use { it.write(bytes) }

            val status = connection.responseCode
            if (status == HttpsURLConnection.HTTP_NO_CONTENT) return null
            if (status != HttpsURLConnection.HTTP_OK) {
                throw RemoteHttpException(status, "The desktop preview request failed ($status).")
            }
            if (connection.contentType?.substringBefore(';') != "image/jpeg") {
                throw IOException("The desktop returned an invalid preview image type.")
            }
            val sequence = connection.getHeaderField("X-Preview-Sequence")?.toLongOrNull()
                ?: throw IOException("The desktop preview frame has no sequence number.")
            val image = connection.inputStream.use { it.readBytes() }
            if (image.size !in 4..MAXIMUM_PREVIEW_BYTES ||
                image[0] != 0xFF.toByte() || image[1] != 0xD8.toByte() ||
                image[image.lastIndex - 1] != 0xFF.toByte() || image.last() != 0xD9.toByte()) {
                throw IOException("The desktop returned an invalid or oversized JPEG preview.")
            }
            return RemotePreviewFrame(sequence, image)
        } finally {
            connection.disconnect()
        }
    }

    fun revoke(session: StoredRemoteSession) {
        val endpoint = RemoteEndpoint(session.host, session.port, session.fingerprint).validate()
        post(endpoint, "revoke", JSONObject().put("token", session.token))
    }

    private fun post(endpoint: RemoteEndpoint, path: String, body: JSONObject): JSONObject {
        val bytes = body.toString().toByteArray(Charsets.UTF_8)
        val connection = openConnection(endpoint, path)
        try {
            connection.requestMethod = "POST"
            connection.doOutput = true
            connection.connectTimeout = CONNECT_TIMEOUT_MS
            connection.readTimeout = READ_TIMEOUT_MS
            connection.setRequestProperty("Content-Type", "application/json; charset=utf-8")
            connection.setRequestProperty("Cache-Control", "no-store")
            connection.setFixedLengthStreamingMode(bytes.size)
            connection.outputStream.use { it.write(bytes) }

            val status = connection.responseCode
            val responseStream = if (status in 200..299) connection.inputStream else connection.errorStream
            val responseText = responseStream?.bufferedReader(Charsets.UTF_8)?.use { it.readText() }.orEmpty()
            val response = try {
                JSONObject(responseText)
            } catch (_: Exception) {
                throw RemoteHttpException(status, "The desktop returned an invalid response ($status).")
            }
            if (status !in 200..299) {
                throw RemoteHttpException(status, response.optString("error").ifBlank { "Desktop request failed ($status)." })
            }
            return response
        } finally {
            connection.disconnect()
        }
    }

    private fun openConnection(endpoint: RemoteEndpoint, path: String): HttpsURLConnection {
        val url = URL("https://${endpoint.host}:${endpoint.port}/v1/$path")
        val trustManager = PinnedCertificateTrustManager(endpoint.fingerprint)
        val context = SSLContext.getInstance("TLS")
        context.init(null, arrayOf(trustManager), SecureRandom())
        return (url.openConnection() as HttpsURLConnection).apply {
            sslSocketFactory = context.socketFactory
            hostnameVerifier = HostnameVerifier { host, session ->
                HttpsURLConnection.getDefaultHostnameVerifier().verify(host, session)
            }
            instanceFollowRedirects = false
            useCaches = false
        }
    }

    private class PinnedCertificateTrustManager(fingerprint: String) : X509TrustManager {
        private val expected = fingerprint.chunked(2).map { it.toInt(16).toByte() }.toByteArray()

        override fun checkClientTrusted(chain: Array<out X509Certificate>, authType: String) {
            throw CertificateException("Client certificates are not used by this service.")
        }

        override fun checkServerTrusted(chain: Array<out X509Certificate>, authType: String) {
            val leaf = chain.firstOrNull() ?: throw CertificateException("The desktop did not present a TLS certificate.")
            leaf.checkValidity()
            val actual = MessageDigest.getInstance("SHA-256").digest(leaf.encoded)
            if (!MessageDigest.isEqual(expected, actual)) {
                throw CertificateException("The desktop TLS fingerprint does not match the pinned value.")
            }
        }

        override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
    }

    private companion object {
        const val PROTOCOL_VERSION = 1
        const val CONNECT_TIMEOUT_MS = 5_000
        const val READ_TIMEOUT_MS = 8_000
        const val MAXIMUM_PREVIEW_BYTES = 1_048_576
    }
}
