package com.soundlink

import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.ServiceConnection
import android.os.Build
import android.os.Bundle
import android.os.IBinder
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.core.content.ContextCompat
import com.soundlink.discovery.NsdDiscoveryService
import com.soundlink.service.AudioStreamService
import com.soundlink.ui.screens.HomeScreen
import com.soundlink.ui.screens.PairScreen
import com.soundlink.ui.screens.StreamScreen
import com.soundlink.ui.theme.SoundLinkTheme
import kotlinx.coroutines.launch

class MainActivity : ComponentActivity() {

    private var audioService: AudioStreamService? = null
    private var serviceBound = false
    private var serviceReady by mutableStateOf(false)
    private lateinit var nsdDiscovery: NsdDiscoveryService

    private val serviceConnection = object : ServiceConnection {
        override fun onServiceConnected(name: ComponentName?, binder: IBinder?) {
            audioService = (binder as AudioStreamService.LocalBinder).getService()
            serviceBound = true
            serviceReady = true
        }

        override fun onServiceDisconnected(name: ComponentName?) {
            audioService = null
            serviceBound = false
            serviceReady = false
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        nsdDiscovery = NsdDiscoveryService(this)

        val serviceIntent = Intent(this, AudioStreamService::class.java)
        bindService(serviceIntent, serviceConnection, Context.BIND_AUTO_CREATE)

        setContent {
            SoundLinkTheme {
                Surface(
                    modifier = Modifier.fillMaxSize(),
                    color = MaterialTheme.colorScheme.background
                ) {
                    SoundLinkApp()
                }
            }
        }
    }

    override fun onStart() {
        super.onStart()
        nsdDiscovery.startDiscovery()
    }

    override fun onStop() {
        super.onStop()
        nsdDiscovery.stopDiscovery()
    }

    override fun onDestroy() {
        super.onDestroy()
        if (serviceBound) {
            unbindService(serviceConnection)
            serviceBound = false
        }
        serviceReady = false
    }

    @Composable
    private fun SoundLinkApp() {
        var screen by remember { mutableStateOf<Screen>(Screen.Home) }
        var servers by remember { mutableStateOf(listOf<NsdDiscoveryService.DiscoveredServer>()) }
        var isConnecting by remember { mutableStateOf(false) }
        var pairError by remember { mutableStateOf<String?>(null) }
        var selectedHost by remember { mutableStateOf("") }
        var selectedPort by remember { mutableStateOf(7359) }
        var selectedServerName by remember { mutableStateOf("") }
        var volume by remember { mutableFloatStateOf(1.0f) }
        var latency by remember { mutableLongStateOf(0L) }
        var isStreaming by remember { mutableStateOf(false) }

        val scope = rememberCoroutineScope()
        val isServiceReady = serviceReady

        DisposableEffect(Unit) {
            nsdDiscovery.onServerFound = { servers = nsdDiscovery.getServers() }
            nsdDiscovery.onServerLost = { servers = nsdDiscovery.getServers() }

            onDispose {
                nsdDiscovery.onServerFound = null
                nsdDiscovery.onServerLost = null
            }
        }

        DisposableEffect(isServiceReady) {
            val service = audioService
            if (service != null) {
                val updateState = {
                    this@MainActivity.runOnUiThread {
                        isStreaming = service.isStreaming
                        latency = service.latencyMs
                        volume = service.playbackVolume
                        if (service.isStreaming) {
                            screen = Screen.Stream
                            if (service.currentServerName.isNotBlank()) {
                                selectedServerName = service.currentServerName
                            }
                        } else if (screen is Screen.Stream) {
                            screen = Screen.Home
                        }
                    }
                }

                service.onStateChanged = updateState
                updateState()
            }

            onDispose {
                if (audioService === service) {
                    service?.onStateChanged = null
                }
            }
        }

        when (screen) {
            is Screen.Home -> {
                HomeScreen(
                    servers = servers,
                    isDiscovering = nsdDiscovery.isDiscovering,
                    onRefresh = {
                        nsdDiscovery.stopDiscovery()
                        nsdDiscovery.startDiscovery()
                    },
                    onServerSelected = { server ->
                        selectedHost = server.host
                        selectedPort = server.port
                        selectedServerName = server.machineName.ifEmpty { server.name }
                        pairError = null
                        screen = Screen.Pair
                    },
                    onManualConnect = { host, port ->
                        selectedHost = host
                        selectedPort = port
                        selectedServerName = host
                        pairError = null
                        screen = Screen.Pair
                    },
                    onUsbConnect = {
                        selectedHost = "127.0.0.1"
                        selectedPort = 7359
                        selectedServerName = "USB (ADB)"
                        pairError = null
                        screen = Screen.Pair
                    }
                )
            }

            is Screen.Pair -> {
                PairScreen(
                    serverName = selectedServerName,
                    isConnecting = isConnecting,
                    canSubmit = isServiceReady,
                    error = pairError,
                    onPinSubmit = { pin ->
                        isConnecting = true
                        pairError = null
                        scope.launch {
                            val service = audioService
                            if (service == null) {
                                isConnecting = false
                                pairError = "Audio service is still initializing. Try again."
                                return@launch
                            }

                            startStreamingService()

                            val success = service.connectAndStream(
                                selectedHost,
                                selectedPort,
                                pin,
                                Build.MODEL
                            )

                            isConnecting = false
                            if (success) {
                                isStreaming = true
                                screen = Screen.Stream
                            } else {
                                pairError = "Connection failed. Check the PIN and try again."
                            }
                        }
                    },
                    onCancel = { screen = Screen.Home }
                )
            }

            is Screen.Stream -> {
                StreamScreen(
                    serverName = audioService?.currentServerName ?: selectedServerName,
                    latencyMs = latency,
                    isStreaming = isStreaming,
                    volume = volume,
                    onVolumeChange = { newVolume ->
                        volume = newVolume
                        audioService?.setPlaybackVolume(newVolume)
                    },
                    onDisconnect = {
                        audioService?.stopStreaming()
                        isStreaming = false
                        screen = Screen.Home
                    }
                )
            }
        }
    }

    private fun startStreamingService() {
        val serviceIntent = Intent(this, AudioStreamService::class.java)
        ContextCompat.startForegroundService(this, serviceIntent)
    }

    private sealed class Screen {
        data object Home : Screen()
        data object Pair : Screen()
        data object Stream : Screen()
    }
}
