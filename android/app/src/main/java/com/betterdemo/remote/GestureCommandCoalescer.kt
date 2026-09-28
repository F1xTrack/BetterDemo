package com.betterdemo.remote

import android.os.Handler
import android.os.Looper

data class TransformValue(val zoom: Float, val panX: Float, val panY: Float)

internal interface GestureCallbackScheduler {
    fun removeCallbacks(callback: Runnable)
    fun post(callback: Runnable)
    fun postDelayed(callback: Runnable, delayMs: Long)
}

private class MainThreadGestureScheduler : GestureCallbackScheduler {
    private val handler = Handler(Looper.getMainLooper())

    override fun removeCallbacks(callback: Runnable) {
        handler.removeCallbacks(callback)
    }

    override fun post(callback: Runnable) {
        handler.post(callback)
    }

    override fun postDelayed(callback: Runnable, delayMs: Long) {
        handler.postDelayed(callback, delayMs)
    }
}

/** Bounds updates during a gesture and sends the final absolute transform on lift. */
internal class GestureCommandCoalescer(
    private val send: (TransformValue) -> Unit,
    private val intervalMs: Long = 120L,
    private val scheduler: GestureCallbackScheduler = MainThreadGestureScheduler(),
) {
    private var pending: TransformValue? = null
    private val flushTask = Runnable {
        val value = pending
        pending = null
        if (value != null) send(value)
    }

    fun offer(value: TransformValue, gestureFinished: Boolean) {
        pending = value
        scheduler.removeCallbacks(flushTask)
        if (gestureFinished) scheduler.post(flushTask) else scheduler.postDelayed(flushTask, intervalMs)
    }

    fun cancel() {
        scheduler.removeCallbacks(flushTask)
        pending = null
    }
}
