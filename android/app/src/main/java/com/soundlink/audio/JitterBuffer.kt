package com.soundlink.audio

import android.util.Log
import java.util.concurrent.PriorityBlockingQueue

/**
 * Adaptive jitter buffer that reorders and buffers incoming audio packets
 * to smooth out network jitter while minimizing latency.
 */
class JitterBuffer(
    /** Target buffer size in packets (e.g., 3 = 30ms at 10ms frames) */
    private var targetSize: Int = 3
) {
    companion object {
        private const val TAG = "JitterBuffer"
        private const val MIN_TARGET = 2   // 20ms minimum
        private const val MAX_TARGET = 8   // 80ms maximum
        private const val ADAPT_INTERVAL = 100 // packets between adaptation checks
    }

    data class AudioFrame(
        val sequenceNumber: Long,
        val timestamp: Long,
        val opusData: ByteArray,
        val opusLength: Int,
        val receiveTime: Long = System.currentTimeMillis()
    ) : Comparable<AudioFrame> {
        override fun compareTo(other: AudioFrame): Int =
            sequenceNumber.compareTo(other.sequenceNumber)
    }

    private val queue = PriorityBlockingQueue<AudioFrame>(32)
    private var nextExpectedSeq: Long = -1
    private var packetsReceived = 0L
    private var packetsLost = 0L
    private var totalJitter = 0L
    private var lastReceiveTime = 0L
    private var adaptCounter = 0

    val currentSize: Int get() = queue.size
    val lossRate: Double get() {
        val total = packetsReceived + packetsLost
        return if (total > 0) packetsLost.toDouble() / total else 0.0
    }

    /**
     * Adds a received packet to the buffer.
     */
    fun push(sequenceNumber: Long, timestamp: Long, opusData: ByteArray, opusLength: Int) {
        val now = System.currentTimeMillis()

        // Calculate jitter for adaptive sizing
        if (lastReceiveTime > 0) {
            val interArrival = now - lastReceiveTime
            totalJitter += kotlin.math.abs(interArrival - 10) // expected 10ms between frames
        }
        lastReceiveTime = now
        packetsReceived++

        // Drop very old packets (more than 200ms behind)
        if (nextExpectedSeq > 0 && sequenceNumber < nextExpectedSeq - 20) {
            return // too old, discard
        }

        val data = ByteArray(opusLength)
        System.arraycopy(opusData, 0, data, 0, opusLength)
        queue.offer(AudioFrame(sequenceNumber, timestamp, data, opusLength, now))

        // Periodically adapt buffer size
        adaptCounter++
        if (adaptCounter >= ADAPT_INTERVAL) {
            adaptBufferSize()
            adaptCounter = 0
        }
    }

    /**
     * Pops the next frame to decode, or null if buffer isn't ready.
     * Returns frames in sequence order.
     */
    fun pop(): AudioFrame? {
        // Wait for buffer to fill to target before starting
        if (nextExpectedSeq < 0) {
            if (queue.size < targetSize) return null
            val first = queue.peek() ?: return null
            nextExpectedSeq = first.sequenceNumber
        }

        // Check if we have the next expected packet
        val head = queue.peek() ?: return null

        return if (head.sequenceNumber <= nextExpectedSeq) {
            val frame = queue.poll()
            if (frame != null) {
                // Count skipped packets as lost
                if (frame.sequenceNumber > nextExpectedSeq) {
                    packetsLost += (frame.sequenceNumber - nextExpectedSeq)
                }
                nextExpectedSeq = frame.sequenceNumber + 1
            }
            frame
        } else if (queue.size > targetSize + 2) {
            // Buffer growing too large — skip ahead
            packetsLost++
            nextExpectedSeq = head.sequenceNumber
            queue.poll()?.also {
                nextExpectedSeq = it.sequenceNumber + 1
            }
        } else {
            // Packet not yet arrived — null means "generate silence" (PLC)
            nextExpectedSeq++
            packetsLost++
            null
        }
    }

    fun reset() {
        queue.clear()
        nextExpectedSeq = -1
        packetsReceived = 0
        packetsLost = 0
        totalJitter = 0
        lastReceiveTime = 0
        adaptCounter = 0
    }

    private fun adaptBufferSize() {
        val avgJitter = if (packetsReceived > 0) totalJitter.toDouble() / packetsReceived else 0.0
        val loss = lossRate

        val newTarget = when {
            loss > 0.05 || avgJitter > 15 -> (targetSize + 1).coerceAtMost(MAX_TARGET)
            loss < 0.01 && avgJitter < 5 -> (targetSize - 1).coerceAtLeast(MIN_TARGET)
            else -> targetSize
        }

        if (newTarget != targetSize) {
            Log.d(TAG, "Adaptive buffer: $targetSize → $newTarget (jitter=${avgJitter.toInt()}ms, loss=${(loss*100).toInt()}%)")
            targetSize = newTarget
        }

        // Reset counters for next adaptation window
        totalJitter = 0
        packetsReceived = 0
        packetsLost = 0
    }
}
