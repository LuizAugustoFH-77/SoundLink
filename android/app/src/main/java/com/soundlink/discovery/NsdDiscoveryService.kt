package com.soundlink.discovery

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.util.Log

/**
 * Discovers SoundLink servers on the local network using Android NSD (mDNS/DNS-SD).
 * Looks for service type "_soundlink._tcp."
 */
class NsdDiscoveryService(private val context: Context) {
    companion object {
        private const val TAG = "NsdDiscovery"
        private const val SERVICE_TYPE = "_soundlink._tcp."
    }

    data class DiscoveredServer(
        val name: String,
        val host: String,
        val port: Int,
        val machineName: String = "",
        val version: String = ""
    )

    private var nsdManager: NsdManager? = null
    private var discoveryListener: NsdManager.DiscoveryListener? = null
    private val servers = mutableMapOf<String, DiscoveredServer>()

    @Volatile
    var isDiscovering = false
        private set

    var onServerFound: ((DiscoveredServer) -> Unit)? = null
    var onServerLost: ((String) -> Unit)? = null

    fun startDiscovery() {
        if (isDiscovering) return

        nsdManager = context.getSystemService(Context.NSD_SERVICE) as NsdManager

        discoveryListener = object : NsdManager.DiscoveryListener {
            override fun onDiscoveryStarted(serviceType: String) {
                Log.i(TAG, "Discovery started for $serviceType")
                isDiscovering = true
            }

            override fun onServiceFound(serviceInfo: NsdServiceInfo) {
                Log.i(TAG, "Service found: ${serviceInfo.serviceName}")
                // Must resolve to get host/port
                resolveService(serviceInfo)
            }

            override fun onServiceLost(serviceInfo: NsdServiceInfo) {
                Log.i(TAG, "Service lost: ${serviceInfo.serviceName}")
                servers.remove(serviceInfo.serviceName)
                onServerLost?.invoke(serviceInfo.serviceName)
            }

            override fun onDiscoveryStopped(serviceType: String) {
                Log.i(TAG, "Discovery stopped")
                isDiscovering = false
            }

            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) {
                Log.e(TAG, "Discovery start failed: error $errorCode")
                isDiscovering = false
            }

            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) {
                Log.e(TAG, "Discovery stop failed: error $errorCode")
            }
        }

        nsdManager!!.discoverServices(SERVICE_TYPE, NsdManager.PROTOCOL_DNS_SD, discoveryListener)
    }

    fun stopDiscovery() {
        if (!isDiscovering) return
        try {
            nsdManager?.stopServiceDiscovery(discoveryListener)
        } catch (e: Exception) {
            Log.w(TAG, "Error stopping discovery", e)
        }
        isDiscovering = false
        servers.clear()
    }

    fun getServers(): List<DiscoveredServer> = servers.values.toList()

    private fun resolveService(serviceInfo: NsdServiceInfo) {
        nsdManager?.resolveService(serviceInfo, object : NsdManager.ResolveListener {
            override fun onResolveFailed(info: NsdServiceInfo, errorCode: Int) {
                Log.e(TAG, "Resolve failed for ${info.serviceName}: error $errorCode")
            }

            override fun onServiceResolved(info: NsdServiceInfo) {
                val host = info.host?.hostAddress ?: return
                val port = info.port

                // Extract TXT record attributes
                val attrs = info.attributes
                val machine = attrs["machine"]?.let { String(it) } ?: info.serviceName
                val version = attrs["version"]?.let { String(it) } ?: "?"

                val server = DiscoveredServer(
                    name = info.serviceName,
                    host = host,
                    port = port,
                    machineName = machine,
                    version = version
                )

                servers[info.serviceName] = server
                Log.i(TAG, "Resolved: ${server.name} at ${server.host}:${server.port}")
                onServerFound?.invoke(server)
            }
        })
    }
}
