package com.betterdemo.remote

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class GestureCommandCoalescerTests {
    @Test
    fun frequent_gesture_updates_are_throttled_and_keep_the_latest_value() {
        val sent = mutableListOf<TransformValue>()
        val scheduler = ManualGestureScheduler()
        val coalescer = GestureCommandCoalescer(
            send = { sent.add(it) },
            intervalMs = 120,
            scheduler = scheduler,
        )

        repeat(100) { index ->
            coalescer.offer(TransformValue(1f + index / 100f, index / 100f, -index / 100f), gestureFinished = false)
        }

        assertTrue(sent.isEmpty())
        assertEquals(1, scheduler.pendingCount)
        scheduler.advanceBy(119)
        assertTrue(sent.isEmpty())
        scheduler.advanceBy(1)

        assertEquals(1, sent.size)
        assertEquals(TransformValue(1.99f, 0.99f, -0.99f), sent.single())
    }

    @Test
    fun gesture_end_sends_the_exact_final_transform_once_without_waiting_for_the_interval() {
        val sent = mutableListOf<TransformValue>()
        val scheduler = ManualGestureScheduler()
        val coalescer = GestureCommandCoalescer(
            send = { sent.add(it) },
            intervalMs = 120,
            scheduler = scheduler,
        )
        val final = TransformValue(2.75f, -0.4f, 0.8f)

        coalescer.offer(TransformValue(2.5f, -0.35f, 0.7f), gestureFinished = false)
        coalescer.offer(final, gestureFinished = true)
        scheduler.runCurrent()
        scheduler.advanceBy(120)

        assertEquals(listOf(final), sent)
    }

    @Test
    fun cancel_discards_a_queued_gesture_update() {
        val sent = mutableListOf<TransformValue>()
        val scheduler = ManualGestureScheduler()
        val coalescer = GestureCommandCoalescer(send = { sent.add(it) }, scheduler = scheduler)
        coalescer.offer(TransformValue(3f, 0.2f, -0.1f), gestureFinished = false)

        coalescer.cancel()
        scheduler.advanceBy(500)

        assertTrue(sent.isEmpty())
        assertEquals(0, scheduler.pendingCount)
    }

    private class ManualGestureScheduler : GestureCallbackScheduler {
        private data class Scheduled(val dueAtMs: Long, val callback: Runnable)

        private val scheduled = mutableListOf<Scheduled>()
        private var nowMs = 0L

        val pendingCount: Int get() = scheduled.size

        override fun removeCallbacks(callback: Runnable) {
            scheduled.removeAll { it.callback === callback }
        }

        override fun post(callback: Runnable) {
            scheduled += Scheduled(nowMs, callback)
        }

        override fun postDelayed(callback: Runnable, delayMs: Long) {
            scheduled += Scheduled(nowMs + delayMs, callback)
        }

        fun runCurrent() = advanceTo(nowMs)

        fun advanceBy(deltaMs: Long) {
            advanceTo(nowMs + deltaMs)
        }

        private fun advanceTo(targetMs: Long) {
            while (true) {
                val next = scheduled.minByOrNull(Scheduled::dueAtMs) ?: break
                if (next.dueAtMs > targetMs) break

                scheduled.remove(next)
                nowMs = next.dueAtMs
                next.callback.run()
            }

            nowMs = targetMs
        }
    }
}
