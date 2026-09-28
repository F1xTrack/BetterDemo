package com.betterdemo.remote

import android.graphics.BitmapFactory
import android.content.Intent
import android.content.res.Configuration
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.Image
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.border
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.calculatePan
import androidx.compose.foundation.gestures.calculateZoom
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Slider
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.HorizontalDivider
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.Modifier
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import java.util.UUID
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import kotlinx.coroutines.launch
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.withContext
import com.google.mlkit.vision.codescanner.GmsBarcodeScanning
import org.json.JSONObject

class MainActivity : ComponentActivity() {
    private var pairingLink by mutableStateOf<String?>(null)
    private var pairingScanRevision by mutableIntStateOf(0)
    private var scannerMessage by mutableStateOf<String?>(null)
    private lateinit var tokenStore: EncryptedTokenStore
    private val remoteClient = RemoteClient()
    private val networkExecutor: ExecutorService = Executors.newSingleThreadExecutor()
    private val mainHandler = Handler(Looper.getMainLooper())

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        pairingLink = intent?.dataString
        tokenStore = EncryptedTokenStore(this)
        val initialSession = tokenStore.load()
        val initialPending = tokenStore.loadPending()
        setContent {
            MaterialTheme(colorScheme = StudioColorScheme) {
                Surface(modifier = Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
                    RemoteScreen(
                        initialSession = initialSession,
                        initialPending = initialPending,
                        pairingLink = pairingLink,
                        pairingScanRevision = pairingScanRevision,
                        scannerMessage = scannerMessage,
                        onScanPairingQr = ::startPairingQrScan,
                        tokenStore = tokenStore,
                        remoteClient = remoteClient,
                        networkExecutor = networkExecutor,
                        mainHandler = mainHandler,
                    )
                }
            }
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        pairingLink = intent.dataString
        pairingScanRevision++
    }

    private fun startPairingQrScan() {
        scannerMessage = null
        try {
            GmsBarcodeScanning.getClient(this).startScan()
                .addOnSuccessListener { barcode ->
                    val rawValue = barcode.rawValue
                    if (rawValue.isNullOrBlank()) {
                        scannerMessage = "The QR code was empty. Try scanning the code on the desktop again."
                    } else {
                        pairingLink = rawValue
                        pairingScanRevision++
                    }
                }
                .addOnFailureListener {
                    scannerMessage = "The QR scanner could not start. Check Google Play services or enter the pairing details manually."
                }
        } catch (_: Exception) {
            scannerMessage = "The QR scanner could not start. Check Google Play services or enter the pairing details manually."
        }
    }

    override fun onDestroy() {
        networkExecutor.shutdown()
        super.onDestroy()
    }
}

@Composable
private fun RemoteScreen(
    initialSession: StoredRemoteSession?,
    initialPending: StoredPendingCommand?,
    pairingLink: String?,
    pairingScanRevision: Int,
    scannerMessage: String?,
    onScanPairingQr: () -> Unit,
    tokenStore: EncryptedTokenStore,
    remoteClient: RemoteClient,
    networkExecutor: ExecutorService,
    mainHandler: Handler,
) {
    var host by remember { mutableStateOf(initialSession?.host ?: "") }
    var portText by remember { mutableStateOf((initialSession?.port ?: 47829).toString()) }
    var pairingCode by remember { mutableStateOf("") }
    var fingerprint by remember { mutableStateOf(initialSession?.fingerprint ?: "") }
    var session by remember { mutableStateOf(initialSession) }
    var pending by remember { mutableStateOf(initialPending) }
    var isBusy by remember { mutableStateOf(false) }
    var isStale by remember { mutableStateOf(initialPending != null || initialSession != null) }
    var status by remember {
        mutableStateOf(
            when {
                initialPending != null -> "A command is waiting for an acknowledgement. Retry it to keep command ordering."
                initialSession != null -> "Paired with ${initialSession.host}. Synchronizing desktop state."
                else -> "Enter the desktop's private IPv4 address, one-time code, and TLS fingerprint."
            },
        )
    }
    var acknowledgement by remember { mutableStateOf("") }
    var mode by remember { mutableStateOf("") }
    var zoom by remember { mutableFloatStateOf(1f) }
    var panX by remember { mutableFloatStateOf(0f) }
    var panY by remember { mutableFloatStateOf(0f) }
    var cameraAngle by remember { mutableFloatStateOf(0f) }
    var cameraScale by remember { mutableFloatStateOf(0.24f) }
    var cameraMargin by remember { mutableFloatStateOf(28f) }
    var blurEnabled by remember { mutableStateOf(false) }
    var blurRadius by remember { mutableFloatStateOf(8f) }
    var outputVisible by remember { mutableStateOf(true) }
    var previewImage by remember { mutableStateOf<ImageBitmap?>(null) }
    var previewSequence by remember { mutableStateOf(0L) }
    var previewUpdatedAt by remember { mutableStateOf(0L) }
    var previewPulse by remember { mutableStateOf(0L) }
    var previewRequestFailed by remember { mutableStateOf(false) }
    var sourceVisibility by remember { mutableStateOf<Map<String, Boolean>>(emptyMap()) }
    var selectedSourceId by remember { mutableStateOf("") }
    var showConnection by remember { mutableStateOf(initialSession == null) }
    var showManualConnection by remember { mutableStateOf(false) }
    var deferredTransform by remember { mutableStateOf<TransformValue?>(null) }
    var viewportSize by remember { mutableStateOf(IntSize.Zero) }
    val scrollState = rememberScrollState()
    val scrollScope = rememberCoroutineScope()
    val controlsEnabled = session != null && !isBusy && !isStale

    LaunchedEffect(pairingLink, pairingScanRevision) {
        if (pairingLink.isNullOrBlank()) return@LaunchedEffect
        val scanned = try {
            PairingQrPayload.parse(pairingLink)
        } catch (failure: IllegalArgumentException) {
            status = failure.message ?: "This is not a valid BetterDemo pairing QR."
            return@LaunchedEffect
        }
        if (session != null) {
            status = "Unpair the current desktop before pairing another one."
            showConnection = true
            return@LaunchedEffect
        }
        host = scanned.endpoint.host
        portText = scanned.endpoint.port.toString()
        pairingCode = scanned.oneTimeCode
        fingerprint = scanned.endpoint.fingerprint
        showManualConnection = false
        showConnection = true
        status = "Desktop found. Tap PAIR DESKTOP to finish the secure connection."
    }

    LaunchedEffect(scannerMessage) {
        if (!scannerMessage.isNullOrBlank()) status = scannerMessage
    }

    fun applySnapshot(snapshot: RemoteSnapshot) {
        mode = snapshot.mode
        zoom = snapshot.zoom.coerceIn(1f, 8f)
        panX = snapshot.panX.coerceIn(-4f, 4f)
        panY = snapshot.panY.coerceIn(-4f, 4f)
        cameraAngle = snapshot.cameraAngle
        cameraScale = snapshot.cameraScale.coerceIn(0.05f, 0.75f)
        cameraMargin = snapshot.cameraMargin.coerceIn(0f, 300f)
        blurEnabled = snapshot.blurEnabled
        blurRadius = snapshot.blurRadius.coerceIn(1f, 32f)
        outputVisible = snapshot.outputVisible
        sourceVisibility = snapshot.layerVisibility
    }

    fun sendCommand(
        commandName: String,
        payload: JSONObject,
        retry: StoredPendingCommand? = null,
    ) {
        val activeSession = session
        if (activeSession == null) {
            status = "Pair with a desktop before sending commands."
            return
        }
        if (isBusy) return
        if (isStale && retry == null && (commandName != "getState" || pending != null)) {
            status = "Retry the pending command or unpair before sending another command."
            return
        }

        val command = retry ?: StoredPendingCommand(
            activeSession.nextSequence,
            commandName,
            UUID.randomUUID().toString(),
            payload.toString(),
        )
        if (retry == null) {
            tokenStore.savePending(command)
            pending = command
        }
        isBusy = true
        status = if (retry == null) "Sending $commandName…" else "Retrying the last command…"

        networkExecutor.execute {
            try {
                val result = remoteClient.sendCommand(
                    activeSession,
                    command.sequence,
                    command.command,
                    JSONObject(command.payloadJson),
                    command.idempotencyKey,
                )
                val accepted = result.acknowledgement == "Accepted" || result.acknowledgement == "Duplicate"
                if (accepted) {
                    activeSession.nextSequence = command.sequence + 1
                    tokenStore.saveNextSequence(activeSession.nextSequence)
                    tokenStore.clearPending()
                }
                mainHandler.post {
                    isBusy = false
                    acknowledgement = "${result.acknowledgement} · state ${result.stateSequence}"
                    applySnapshot(result.snapshot)
                    if (accepted) {
                        pending = null
                        isStale = false
                        status = if (result.acknowledgement == "Duplicate") "The desktop confirmed the retried command." else "Desktop state is synchronized."
                        val deferred = deferredTransform
                        deferredTransform = null
                        if (deferred != null) {
                            sendCommand("setTransform", transformPayload(deferred))
                        }
                    } else {
                        isStale = true
                        status = if (result.acknowledgement == "Stale") {
                            "Command sequence is out of sync. Restart desktop LAN remote, then pair again."
                        } else {
                            result.errorMessage ?: "The desktop rejected this command (${result.errorCode ?: "unknown error"})."
                        }
                    }
                }
            } catch (failure: Exception) {
                mainHandler.post {
                    isBusy = false
                    isStale = true
                    status = "Connection failed. State may be stale; retry the same pending command."
                    acknowledgement = failure.javaClass.simpleName
                }
            }
        }
    }

    fun sendTransform(value: TransformValue) {
        if (isBusy) {
            deferredTransform = value
        } else {
            sendCommand("setTransform", transformPayload(value))
        }
    }

    fun toggleSourceVisibility(source: StudioSource) {
        val nextVisibility = !(sourceVisibility[source.layerId] ?: true)
        sourceVisibility = sourceVisibility + (source.layerId to nextVisibility)
        sendCommand(
            "setLayerVisibility",
            JSONObject().put("layerId", source.layerId).put("visible", nextVisibility),
        )
    }

    val latestTransformCommand = rememberUpdatedState<(TransformValue) -> Unit> { value -> sendTransform(value) }
    val coalescer = remember {
        GestureCommandCoalescer(send = { value -> latestTransformCommand.value(value) })
    }
    DisposableEffect(coalescer) {
        onDispose { coalescer.cancel() }
    }

    val latestGestureUpdate = rememberUpdatedState<(TransformValue, Boolean) -> Unit> { value, finished ->
        val bounded = TransformValue(value.zoom.coerceIn(1f, 8f), value.panX.coerceIn(-4f, 4f), value.panY.coerceIn(-4f, 4f))
        zoom = bounded.zoom
        panX = bounded.panX
        panY = bounded.panY
        coalescer.offer(bounded, finished)
    }

    fun pairDesktop() {
        val port = portText.toIntOrNull()
        if (port == null) {
            status = "Enter a valid port number."
            return
        }
        val endpoint = try {
            RemoteEndpoint(host.trim(), port, fingerprint.trim()).validate()
        } catch (failure: IllegalArgumentException) {
            status = failure.message ?: "Check the desktop address and TLS fingerprint."
            return
        }
        isBusy = true
        status = "Checking the pinned TLS certificate and pairing code…"
        networkExecutor.execute {
            try {
                val paired = remoteClient.pair(endpoint, pairingCode.trim())
                tokenStore.save(endpoint.host, endpoint.port, endpoint.fingerprint, paired.token)
                tokenStore.clearPending()
                val loaded = tokenStore.load()
                mainHandler.post {
                    session = loaded
                    pending = null
                    showConnection = false
                    isStale = false
                    isBusy = false
                    fingerprint = endpoint.fingerprint
                    status = "Paired. Token expires ${paired.expiresAt}."
                    if (loaded != null) sendCommand("getState", JSONObject())
                }
            } catch (failure: Exception) {
                mainHandler.post {
                    isBusy = false
                    status = "Pairing failed. Check that the desktop is running and the code and fingerprint match."
                    acknowledgement = failure.javaClass.simpleName
                }
            }
        }
    }

    fun unpair() {
        val activeSession = session
        if (activeSession == null || isBusy) return
        isBusy = true
        status = "Revoking this paired device…"
        networkExecutor.execute {
            var revoked = false
            try {
                remoteClient.revoke(activeSession)
                revoked = true
            } catch (_: Exception) {
                // The local token is removed even when the desktop is unreachable.
        } finally {
                tokenStore.clear()
            }
            mainHandler.post {
                session = null
                pending = null
                mode = ""
                showConnection = true
                isBusy = false
                isStale = false
                acknowledgement = ""
                status = if (revoked) "Device revoked and its local token deleted." else "Local token deleted. The desktop was unreachable, so server revocation could not be confirmed."
            }
        }
    }

    LaunchedEffect(initialSession?.token) {
        if (initialSession != null && initialPending == null) {
            sendCommand("getState", JSONObject())
        }
    }

    LaunchedEffect(session?.token) {
        val activeSession = session
        previewImage = null
        previewSequence = 0
        previewUpdatedAt = 0
        previewRequestFailed = false
        if (activeSession != null) {
            while (isActive) {
                try {
                    val frame = withContext(Dispatchers.IO) { remoteClient.fetchPreview(activeSession) }
                    previewRequestFailed = false
                    if (frame != null && frame.sequence > previewSequence) {
                        val decoded = withContext(Dispatchers.Default) {
                            BitmapFactory.decodeByteArray(frame.jpegBytes, 0, frame.jpegBytes.size)?.asImageBitmap()
                        }
                        if (decoded != null) {
                            previewImage = decoded
                            previewSequence = frame.sequence
                            previewUpdatedAt = SystemClock.elapsedRealtime()
                        } else {
                            previewRequestFailed = true
                        }
                    }
                    previewPulse = SystemClock.elapsedRealtime()
                    delay(180)
                } catch (_: Exception) {
                    previewRequestFailed = true
                    previewPulse = SystemClock.elapsedRealtime()
                    delay(700)
                }
            }
        }
    }

    LaunchedEffect(session?.token) {
        if (session == null) {
            delay(100)
            scrollState.animateScrollTo(scrollState.maxValue)
        } else {
            scrollState.animateScrollTo(0)
        }
    }

    val latestTransform = rememberUpdatedState(TransformValue(zoom, panX, panY))
    val latestViewportSize = rememberUpdatedState(viewportSize)
    val modeTitle = when (mode) {
        "screen" -> "OBS VIRTUAL CAMERA"
        "physicalCamera" -> "PHYSICAL CAMERA"
        "screenPlusPhysicalCameraCorner" -> "OBS + CAMERA"
        else -> if (mode.isBlank()) "STATE NOT SYNCED" else "BLACK SCENE"
    }
    val previewNow = maxOf(previewPulse, SystemClock.elapsedRealtime())
    val previewAgeMs = if (previewUpdatedAt == 0L) Long.MAX_VALUE else previewNow - previewUpdatedAt
    val previewIsLive = session != null && previewImage != null && previewAgeMs <= 1_500 && !previewRequestFailed
    val previewBadge = when {
        session == null -> "OFFLINE"
        previewRequestFailed -> "PREVIEW ERROR"
        previewImage == null -> "WAITING FOR PREVIEW"
        previewIsLive -> "LIVE · 640×360"
        else -> "PREVIEW STALE"
    }
    val previewBadgeColor = when {
        previewIsLive -> Color(0xFF68D391)
        previewRequestFailed -> Color(0xFFFF7777)
        else -> Color(0xFFFFC46B)
    }
    val studioSources = when (mode) {
        "screen" -> listOf(ObsStudioSource)
        "physicalCamera" -> listOf(PhysicalStudioSource)
        "screenPlusPhysicalCameraCorner" -> listOf(ObsStudioSource, PhysicalStudioSource)
        else -> emptyList()
    }
    val sourcesDetail = when {
        mode.isBlank() -> "AWAITING SYNC"
        studioSources.isEmpty() -> "NO VIDEO INPUTS"
        else -> "${studioSources.size} ${if (studioSources.size == 1) "SOURCE" else "SOURCES"}"
    }
    val displayedSelectedSourceId = studioSources.firstOrNull { it.layerId == selectedSourceId }?.layerId
        ?: studioSources.firstOrNull()?.layerId

    val configuration = LocalConfiguration.current
    val isLandscape = configuration.orientation == Configuration.ORIENTATION_LANDSCAPE
    val previewMaxHeight = (configuration.screenHeightDp * 0.42f).coerceAtLeast(160f).dp
    val programPanel: @Composable ColumnScope.() -> Unit = {
            StudioPanel("PROGRAM", "DESKTOP OUTPUT · 16:9") {
                Box(
                    modifier = Modifier
                        .then(
                            if (isLandscape) {
                                Modifier.heightIn(max = previewMaxHeight)
                                    .aspectRatio(16f / 9f, matchHeightConstraintsFirst = true)
                                    .align(Alignment.CenterHorizontally)
                            } else {
                                Modifier.fillMaxWidth().aspectRatio(16f / 9f)
                            },
                        )
                        .clip(RoundedCornerShape(5.dp))
                        .background(Color(0xFF090B0C))
                        .border(1.dp, Color(0xFF3B4044), RoundedCornerShape(5.dp))
                        .onSizeChanged { viewportSize = it }
                        .pointerInput(controlsEnabled) {
                            if (!controlsEnabled) return@pointerInput
                            awaitEachGesture {
                                awaitFirstDown(requireUnconsumed = false)
                                val initial = latestTransform.value
                                var currentZoom = initial.zoom
                                var currentPanX = initial.panX
                                var currentPanY = initial.panY
                                var twoFingerGesture = false
                                do {
                                    val event = awaitPointerEvent()
                                    if (event.changes.count { it.pressed } >= 2) {
                                        twoFingerGesture = true
                                        val size = latestViewportSize.value
                                        val deltaZoom = event.calculateZoom()
                                        val deltaPan = event.calculatePan()
                                        if (deltaZoom.isFinite() && size.width > 0 && size.height > 0) {
                                            currentZoom = (currentZoom * deltaZoom).coerceIn(1f, 8f)
                                            currentPanX = (currentPanX + deltaPan.x / size.width).coerceIn(-4f, 4f)
                                            currentPanY = (currentPanY + deltaPan.y / size.height).coerceIn(-4f, 4f)
                                            latestGestureUpdate.value(TransformValue(currentZoom, currentPanX, currentPanY), false)
                                        }
                                        event.changes.forEach { change -> if (change.pressed) change.consume() }
                                    }
                                } while (event.changes.any { it.pressed })
                                if (twoFingerGesture) latestGestureUpdate.value(TransformValue(currentZoom, currentPanX, currentPanY), true)
                            }
                        },
                ) {
                    val currentPreview = previewImage
                    if (currentPreview != null) {
                        Image(
                            bitmap = currentPreview,
                            contentDescription = "Live desktop Program preview",
                            contentScale = ContentScale.Fit,
                            modifier = Modifier.fillMaxSize(),
                        )
                    } else {
                        Column(Modifier.align(Alignment.Center), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(5.dp)) {
                            Text(if (session == null) "PAIR WITH DESKTOP" else "LIVE PROGRAM PREVIEW", fontSize = 13.sp, fontWeight = FontWeight.Bold, letterSpacing = 0.7.sp, color = Color.White)
                            Text(if (previewRequestFailed) "CHECK DESKTOP CONNECTION" else "WAITING FOR DESKTOP VIDEO", fontSize = 8.sp, letterSpacing = 0.5.sp, color = Color(0xFFADB5BC))
                        }
                    }
                    Row(
                        Modifier.align(Alignment.TopEnd).padding(8.dp)
                            .background(Color(0xCC101315), RoundedCornerShape(4.dp))
                            .padding(horizontal = 8.dp, vertical = 5.dp),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(5.dp),
                    ) {
                        Box(Modifier.size(7.dp).clip(CircleShape).background(previewBadgeColor))
                        Text(previewBadge, color = Color.White, fontSize = 8.sp, fontWeight = FontWeight.Bold)
                    }
                    Row(
                        Modifier.align(Alignment.BottomStart).padding(8.dp)
                            .background(Color(0xCC141719), RoundedCornerShape(4.dp)).padding(horizontal = 7.dp, vertical = 4.dp),
                    ) {
                        Text("${"%.2f".format(zoom)}×", color = Color.White, fontSize = 9.sp, fontWeight = FontWeight.SemiBold)
                    }
                }
                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.SpaceBetween, modifier = Modifier.fillMaxWidth()) {
                    Column {
                        Text(modeTitle, fontSize = 11.sp, fontWeight = FontWeight.Bold)
                        Text("Pinch to zoom · two-finger drag to pan", fontSize = 10.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                    Switch(
                        checked = outputVisible,
                        onCheckedChange = {
                            outputVisible = it
                            sendCommand("setOutputVisibility", JSONObject().put("visible", it))
                        },
                        enabled = controlsEnabled,
                    )
                }
            }

    }
    val studioDocks: @Composable ColumnScope.() -> Unit = {
            StudioPanel("SCENES", if (controlsEnabled) "PROGRAM" else "AWAITING SYNC") {
                Row(horizontalArrangement = Arrangement.spacedBy(7.dp), modifier = Modifier.fillMaxWidth()) {
                    StudioSceneButton("OBS", "screen", mode, controlsEnabled) { mode = it; sendCommand("setMode", JSONObject().put("mode", it)) }
                    StudioSceneButton("CAMERA", "physicalCamera", mode, controlsEnabled) { mode = it; sendCommand("setMode", JSONObject().put("mode", it)) }
                }
                Row(horizontalArrangement = Arrangement.spacedBy(7.dp), modifier = Modifier.fillMaxWidth()) {
                    StudioSceneButton("CORNER", "screenPlusPhysicalCameraCorner", mode, controlsEnabled) { mode = it; sendCommand("setMode", JSONObject().put("mode", it)) }
                    StudioSceneButton("BLACK", "black", mode, controlsEnabled) { mode = it; sendCommand("setMode", JSONObject().put("mode", it)) }
                }
            }

            StudioPanel("SOURCES", sourcesDetail) {
                when {
                    mode.isBlank() -> Text(
                        "Pair with the desktop to load the current scene's sources.",
                        fontSize = 10.sp,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                    studioSources.isEmpty() -> Text(
                        "Black scene · no video inputs",
                        fontSize = 10.sp,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                    else -> studioSources.forEachIndexed { index, source ->
                        if (index > 0) HorizontalDivider(color = MaterialTheme.colorScheme.outline.copy(alpha = 0.4f))
                        StudioSourceRow(
                            source = source,
                            visible = sourceVisibility[source.layerId] ?: true,
                            selected = source.layerId == displayedSelectedSourceId,
                            enabled = controlsEnabled,
                            onSelect = { selectedSourceId = source.layerId },
                            onToggleVisibility = { toggleSourceVisibility(source) },
                        )
                    }
                }
            }

            StudioPanel("TRANSFORM", "${"%.2f".format(zoom)}×") {
                Text("OUTPUT ZOOM", fontSize = 9.sp, fontWeight = FontWeight.Bold, letterSpacing = 0.7.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Slider(
                    value = zoom,
                    onValueChange = { zoom = it },
                    valueRange = 1f..8f,
                    enabled = controlsEnabled,
                    onValueChangeFinished = { sendCommand("setZoom", JSONObject().put("zoom", zoom.toDouble())) },
                )
                Text("Pan  X ${"%.2f".format(panX)}   Y ${"%.2f".format(panY)}", fontSize = 10.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }

            StudioPanel("CAMERA CORNER", "${(cameraScale * 100).toInt()}%") {
                StudioSlider("SIZE", cameraScale, 0.05f..0.75f, controlsEnabled, { cameraScale = it }) {
                    sendCommand("setCameraCorner", cameraCornerPayload(cameraAngle, cameraScale, cameraMargin))
                }
                StudioSlider("MARGIN · PX", cameraMargin, 0f..300f, controlsEnabled, { cameraMargin = it }) {
                    sendCommand("setCameraCorner", cameraCornerPayload(cameraAngle, cameraScale, cameraMargin))
                }
                StudioSlider("ROTATION · °", cameraAngle, -45f..45f, controlsEnabled, { cameraAngle = it }) {
                    sendCommand("setCameraCorner", cameraCornerPayload(cameraAngle, cameraScale, cameraMargin))
                }
            }

            StudioPanel("FILTERS", "BLUR") {
                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.SpaceBetween, modifier = Modifier.fillMaxWidth()) {
                    Column {
                        Text("FULL SCREEN BLUR", fontSize = 11.sp, fontWeight = FontWeight.SemiBold)
                        Text("Radius ${blurRadius.toInt()} px", fontSize = 10.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                    Switch(
                        checked = blurEnabled,
                        onCheckedChange = {
                            blurEnabled = it
                            sendCommand("setBlur", JSONObject().put("enabled", it).put("radius", blurRadius.toDouble()))
                        },
                        enabled = controlsEnabled,
                    )
                }
                StudioSlider("RADIUS", blurRadius, 1f..32f, controlsEnabled, { blurRadius = it }) {
                    sendCommand("setBlur", JSONObject().put("enabled", blurEnabled).put("radius", blurRadius.toDouble()))
                }
            }

            StudioPanel("CONTROLS", "DESKTOP OUTPUT") {
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.fillMaxWidth()) {
                    StudioAction("RESET TRANSFORM", controlsEnabled, Modifier.weight(1f)) {
                        zoom = 1f
                        panX = 0f
                        panY = 0f
                        sendCommand("resetTransform", JSONObject())
                    }
                    StudioAction("SYNC STATE", session != null && !isBusy && pending == null, Modifier.weight(1f), secondary = true) {
                        sendCommand("getState", JSONObject())
                    }
                }
                Text(if (outputVisible) "OutputWindow visible" else "OutputWindow hidden", fontSize = 10.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }

            StudioPanel("CONNECTION", if (session == null) "PAIR DESKTOP" else "PRIVATE LAN · TLS") {
                if (session == null) {
                    StudioAction("SCAN DESKTOP QR", !isBusy, Modifier.fillMaxWidth(), onClick = onScanPairingQr)
                    Text("On the PC, start LAN remote and point the scanner at its QR code.", fontSize = 10.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    StudioAction(
                        if (showManualConnection) "HIDE MANUAL DETAILS" else "ENTER DETAILS MANUALLY",
                        !isBusy,
                        Modifier.fillMaxWidth(),
                        secondary = true,
                    ) { showManualConnection = !showManualConnection }
                    if (showManualConnection) {
                        OutlinedTextField(
                            value = host,
                            onValueChange = { host = it },
                            label = { Text("Private IPv4 address") },
                            placeholder = { Text("192.168.1.20") },
                            singleLine = true,
                            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                            modifier = Modifier.fillMaxWidth(),
                        )
                        OutlinedTextField(
                            value = portText,
                            onValueChange = { portText = it.filter(Char::isDigit).take(5) },
                            label = { Text("Port") },
                            singleLine = true,
                            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                            modifier = Modifier.fillMaxWidth(),
                        )
                        OutlinedTextField(
                            value = pairingCode,
                            onValueChange = { pairingCode = it.filter(Char::isDigit).take(6) },
                            label = { Text("One-time code") },
                            singleLine = true,
                            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                            modifier = Modifier.fillMaxWidth(),
                        )
                        OutlinedTextField(
                            value = fingerprint,
                            onValueChange = { fingerprint = it.filter { character -> character.isDigit() || character.lowercaseChar() in 'a'..'f' || character == ':' || character == ' ' }.take(95) },
                            label = { Text("TLS SHA-256 fingerprint") },
                            placeholder = { Text("Copy from the desktop editor") },
                            singleLine = true,
                            modifier = Modifier.fillMaxWidth(),
                        )
                        StudioAction("PAIR DESKTOP", !isBusy, Modifier.fillMaxWidth(), onClick = ::pairDesktop)
                    } else if (pairingCode.length == 6 && fingerprint.isNotBlank()) {
                        StudioAction("PAIR DESKTOP", !isBusy, Modifier.fillMaxWidth(), onClick = ::pairDesktop)
                    }
                } else {
                    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.SpaceBetween, modifier = Modifier.fillMaxWidth()) {
                        Column {
                            Text("Paired with ${session!!.host}:${session!!.port}", fontSize = 12.sp, fontWeight = FontWeight.SemiBold)
                            Text("Certificate-pinned TLS session", fontSize = 9.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                        StudioAction(if (showConnection) "HIDE" else "DETAILS", true, Modifier.width(82.dp), secondary = true) { showConnection = !showConnection }
                    }
                    if (showConnection) {
                        Text("TLS pin · ${session!!.fingerprint.chunked(2).joinToString(":")}", fontSize = 9.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        Row(horizontalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.fillMaxWidth()) {
                            StudioAction("SYNC STATE", !isBusy, Modifier.weight(1f), secondary = true) { sendCommand("getState", JSONObject()) }
                            StudioAction("UNPAIR", !isBusy, Modifier.weight(1f), secondary = true, onClick = ::unpair)
                        }
                    }
                }
                Text(status, fontSize = 10.sp, color = if (isStale) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant)
                if (acknowledgement.isNotBlank()) Text("ACK · $acknowledgement", fontSize = 9.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                if (pending != null) {
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.fillMaxWidth()) {
                        StudioAction("RETRY LAST COMMAND", !isBusy, Modifier.weight(1f)) {
                            sendCommand(pending!!.command, JSONObject(pending!!.payloadJson), pending)
                        }
                        StudioAction("DISCARD", !isBusy, Modifier.weight(1f), secondary = true) {
                            tokenStore.clearPending()
                            pending = null
                            isStale = true
                            status = "Pending command discarded. Pair again if the sequence is out of sync."
                        }
                    }
                }
            }
            Spacer(Modifier.height(12.dp))
    }

    Column(Modifier.fillMaxSize().background(MaterialTheme.colorScheme.background).statusBarsPadding().navigationBarsPadding()) {
        Row(
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 12.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Image(
                painter = painterResource(R.drawable.betterdemo_brand),
                contentDescription = "BetterDemo",
                modifier = Modifier.size(38.dp).clip(RoundedCornerShape(10.dp)),
                contentScale = ContentScale.Crop,
            )
            Spacer(Modifier.width(11.dp))
            Column {
                Text("BETTERDEMO", fontSize = 18.sp, fontWeight = FontWeight.Bold, letterSpacing = 1.sp)
                Text("STUDIO CONTROL", fontSize = 10.sp, fontWeight = FontWeight.Bold, letterSpacing = 1.4.sp, color = MaterialTheme.colorScheme.primary)
            }
            Spacer(Modifier.weight(1f))
            Surface(
                onClick = { scrollScope.launch { scrollState.animateScrollTo(scrollState.maxValue) } },
                shape = RoundedCornerShape(5.dp),
                color = MaterialTheme.colorScheme.surfaceVariant,
            ) {
                Row(Modifier.padding(horizontal = 9.dp, vertical = 7.dp), verticalAlignment = Alignment.CenterVertically) {
                    Box(Modifier.size(8.dp).background(if (session != null && !isStale) StudioGreen else StudioAmber, CircleShape))
                    Spacer(Modifier.width(7.dp))
                    Text(if (session != null) "${if (isStale) "STALE" else "CONNECTED"}  ↓" else "OFFLINE  ↓", fontSize = 10.sp, fontWeight = FontWeight.Bold, letterSpacing = 0.7.sp)
                }
            }
        }

        if (isLandscape) {
            Row(
                modifier = Modifier.fillMaxSize().padding(horizontal = 12.dp, vertical = 4.dp),
                horizontalArrangement = Arrangement.spacedBy(10.dp),
                verticalAlignment = Alignment.Top,
            ) {
                Column(
                    modifier = Modifier.weight(1.2f).fillMaxHeight(),
                    verticalArrangement = Arrangement.spacedBy(10.dp),
                ) {
                    programPanel()
                }
                Column(
                    modifier = Modifier.weight(1f).fillMaxHeight().verticalScroll(scrollState),
                    verticalArrangement = Arrangement.spacedBy(10.dp),
                ) {
                    studioDocks()
                }
            }
        } else {
            Column(
                modifier = Modifier.fillMaxSize().verticalScroll(scrollState).padding(horizontal = 12.dp, vertical = 4.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                programPanel()
                studioDocks()
            }
        }
    }
}

@Composable
private fun StudioPanel(title: String, detail: String? = null, content: @Composable ColumnScope.() -> Unit) {
    val shape = RoundedCornerShape(7.dp)
    Column(
        Modifier.fillMaxWidth().clip(shape).background(MaterialTheme.colorScheme.surface)
            .border(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.45f), shape).padding(11.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
            Text(title, fontSize = 10.sp, fontWeight = FontWeight.Bold, letterSpacing = 1.sp)
            Spacer(Modifier.weight(1f))
            if (detail != null) Text(detail, fontSize = 8.sp, fontWeight = FontWeight.SemiBold, letterSpacing = 0.4.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        HorizontalDivider(color = MaterialTheme.colorScheme.outline.copy(alpha = 0.5f), thickness = 0.6.dp)
        content()
    }
}

@Composable
private fun RowScope.StudioSceneButton(label: String, value: String, selected: String, enabled: Boolean, onClick: (String) -> Unit) {
    val active = value == selected
    Surface(
        modifier = Modifier.weight(1f).height(48.dp),
        shape = RoundedCornerShape(5.dp),
        color = if (active) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.surfaceVariant,
        contentColor = if (active) MaterialTheme.colorScheme.onPrimary else MaterialTheme.colorScheme.onSurface,
        border = BorderStroke(1.dp, if (active) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.outline.copy(alpha = 0.55f)),
        enabled = enabled,
        onClick = { onClick(value) },
    ) {
        Column(Modifier.fillMaxSize().padding(horizontal = 10.dp), verticalArrangement = Arrangement.Center) {
            Text(label, fontSize = 10.sp, fontWeight = FontWeight.Bold, letterSpacing = 0.5.sp)
            Text(if (active) "PROGRAM" else "SCENE", fontSize = 8.sp, color = if (active) MaterialTheme.colorScheme.onPrimary.copy(alpha = 0.78f) else MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

@Composable
private fun StudioSourceRow(
    source: StudioSource,
    visible: Boolean,
    selected: Boolean,
    enabled: Boolean,
    onSelect: () -> Unit,
    onToggleVisibility: () -> Unit,
) {
    Row(
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(7.dp),
        modifier = Modifier.fillMaxWidth().padding(vertical = 2.dp),
    ) {
        Surface(
            modifier = Modifier.weight(1f),
            shape = RoundedCornerShape(5.dp),
            color = if (selected) MaterialTheme.colorScheme.primary.copy(alpha = 0.12f) else MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.5f),
            border = BorderStroke(
                1.dp,
                if (selected) MaterialTheme.colorScheme.primary.copy(alpha = 0.8f) else MaterialTheme.colorScheme.outline.copy(alpha = 0.35f),
            ),
            enabled = enabled,
            onClick = onSelect,
        ) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(9.dp),
                modifier = Modifier.fillMaxWidth().padding(horizontal = 9.dp, vertical = 8.dp),
            ) {
                Box(Modifier.size(8.dp).background(if (visible) StudioGreen else MaterialTheme.colorScheme.outline, CircleShape))
                Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                    Text(source.name, fontSize = 11.sp, fontWeight = FontWeight.SemiBold)
                    Text(source.detail, fontSize = 8.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                Text(
                    if (visible) "VISIBLE" else "HIDDEN",
                    fontSize = 8.sp,
                    fontWeight = FontWeight.Bold,
                    color = if (visible) StudioGreen else MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
        Surface(
            modifier = Modifier.size(38.dp),
            shape = RoundedCornerShape(5.dp),
            color = MaterialTheme.colorScheme.surfaceVariant,
            border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.5f)),
            enabled = enabled,
            onClick = onToggleVisibility,
        ) {
            Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Text(
                    if (visible) "◉" else "○",
                    fontSize = 17.sp,
                    fontWeight = FontWeight.Bold,
                    color = if (visible) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}

@Composable
private fun StudioSlider(label: String, value: Float, range: ClosedFloatingPointRange<Float>, enabled: Boolean, onValueChange: (Float) -> Unit, onFinished: () -> Unit) {
    val valueLabel = when (label) {
        "SIZE" -> "${(value * 100).toInt()}%"
        "ROTATION · °" -> "${value.toInt()}°"
        else -> value.toInt().toString()
    }
    Column(verticalArrangement = Arrangement.spacedBy(1.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.SpaceBetween, modifier = Modifier.fillMaxWidth()) {
            Text(label, fontSize = 9.sp, fontWeight = FontWeight.Bold, letterSpacing = 0.5.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Text(valueLabel, fontSize = 9.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        Slider(value = value, onValueChange = onValueChange, valueRange = range, enabled = enabled, onValueChangeFinished = onFinished)
    }
}

@Composable
private fun StudioAction(label: String, enabled: Boolean, modifier: Modifier = Modifier, secondary: Boolean = false, onClick: () -> Unit) {
    Surface(
        modifier = modifier.height(38.dp),
        shape = RoundedCornerShape(5.dp),
        color = if (secondary) MaterialTheme.colorScheme.surfaceVariant else MaterialTheme.colorScheme.primary,
        contentColor = if (secondary) MaterialTheme.colorScheme.onSurface else MaterialTheme.colorScheme.onPrimary,
        border = if (secondary) BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.65f)) else null,
        enabled = enabled,
        onClick = onClick,
    ) {
        Box(Modifier.fillMaxSize().padding(horizontal = 8.dp), contentAlignment = Alignment.Center) {
            Text(label, fontSize = 9.sp, fontWeight = FontWeight.Bold, letterSpacing = 0.35.sp)
        }
    }
}

private fun cameraCornerPayload(angle: Float, scale: Float, margin: Float): JSONObject = JSONObject()
    .put("angleDegrees", angle.toDouble())
    .put("scale", scale.toDouble())
    .put("margin", margin.toDouble())

private data class StudioSource(val layerId: String, val name: String, val detail: String)

private val ObsStudioSource = StudioSource(
    "preset:obs-virtual-camera",
    "OBS Virtual Camera",
    "DESKTOP VIDEO SOURCE",
)

private val PhysicalStudioSource = StudioSource(
    "preset:physical-camera",
    "Physical Camera",
    "CAMERA INPUT · DESKTOP",
)

private val StudioGreen = Color(0xFF79C453)
private val StudioAmber = Color(0xFFE0A856)
private val StudioColorScheme = darkColorScheme(
    primary = Color(0xFF5AA9E6),
    onPrimary = Color(0xFF071521),
    secondary = Color(0xFF8EB9D5),
    background = Color(0xFF17191A),
    surface = Color(0xFF222527),
    surfaceVariant = Color(0xFF2C3033),
    surfaceContainer = Color(0xFF222527),
    onSurface = Color(0xFFE1E5E8),
    onSurfaceVariant = Color(0xFF9FA7AD),
    outline = Color(0xFF535A60),
    error = Color(0xFFFF7777),
)

private fun transformPayload(value: TransformValue): JSONObject = JSONObject()
    .put("zoom", value.zoom.toDouble())
    .put("panX", value.panX.toDouble())
    .put("panY", value.panY.toDouble())
