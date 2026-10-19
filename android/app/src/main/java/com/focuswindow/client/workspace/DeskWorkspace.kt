package com.focuswindow.client.workspace

import java.util.UUID

/** Metadata from an authenticated Windows window-list response. Never persisted. */
data class DeskWindow(val handle: String, val title: String, val appLabel: String)

data class DeskTopic(val id: String, val name: String)

/** The logical card ID is private to this live session; [handle] is not a durable identity. */
data class DeskCard(
    val sourceId: String,
    val handle: String,
    val title: String,
    val appLabel: String,
    val topicId: String,
    val x: Float,
    val y: Float,
)

data class DeskSnapshot(val topics: List<DeskTopic>, val cards: List<DeskCard>)

/**
 * Local-only desk state over the existing v3 window-list/focus commands.
 *
 * This is intentionally NOT a capture service. HWNDs are short-lived focus tokens.
 * They are never used as a stable identity, persisted, or passed into a rendering API.
 * Replacement windows receive new sourceIds even if Windows recycles an HWND.
 * Topic/card arrangements survive list refreshes in the same live connection only.
 * Future host source identities will allow durable persistence and reconnection.
 */
class DeskWorkspace(private val newId: () -> String = { UUID.randomUUID().toString() }) {
    companion object {
        const val INBOX = "inbox"
        const val MAX_TOPICS = 12
        const val MAX_WINDOWS = 100
    }

    private val topics = mutableListOf(DeskTopic(INBOX, "Inbox"))
    private val cards = linkedMapOf<String, DeskCard>()

    fun snapshot(): DeskSnapshot = DeskSnapshot(topics.toList(), cards.values.toList())

    fun clearWindows() = cards.clear()

    /** Replace, never append: a disappeared HWND cannot remain an actionable card. */
    fun sync(windows: List<DeskWindow>) {
        val next = linkedMapOf<String, DeskCard>()
        windows.take(MAX_WINDOWS).forEach { entry ->
            if (entry.handle.isBlank() || entry.title.isBlank() || entry.appLabel.isBlank() ||
                next.containsKey(entry.handle)) return@forEach
            val old = cards[entry.handle]
            if (old != null && old.title == entry.title && old.appLabel == entry.appLabel) {
                next[entry.handle] = old
            } else {
                val position = next.size
                next[entry.handle] = DeskCard(
                    sourceId = newId(), handle = entry.handle, title = entry.title,
                    appLabel = entry.appLabel, topicId = INBOX,
                    x = 20f + (position % 2) * 174f,
                    y = 26f + (position / 2) * 154f,
                )
            }
        }
        cards.clear()
        cards.putAll(next)
    }

    fun addTopic(name: String): Boolean {
        val clean = name.trim()
        if (clean.isEmpty() || clean.length > 32 || clean.any(Char::isISOControl) ||
            topics.size >= MAX_TOPICS || topics.any { it.name.equals(clean, ignoreCase = true) }) return false
        topics += DeskTopic(newId(), clean)
        return true
    }

    fun removeTopic(id: String): Boolean {
        if (id == INBOX || topics.none { it.id == id }) return false
        topics.removeAll { it.id == id }
        cards.replaceAll { _, c -> if (c.topicId == id) c.copy(topicId = INBOX) else c }
        return true
    }

    fun moveToTopic(sourceId: String, topicId: String): Boolean {
        if (topics.none { it.id == topicId }) return false
        val item = cards.values.firstOrNull { it.sourceId == sourceId } ?: return false
        cards[item.handle] = item.copy(topicId = topicId)
        return true
    }

    fun move(sourceId: String, deltaX: Float, deltaY: Float): Boolean {
        if (!deltaX.isFinite() || !deltaY.isFinite()) return false
        val item = cards.values.firstOrNull { it.sourceId == sourceId } ?: return false
        cards[item.handle] = item.copy(
            x = (item.x + deltaX).coerceIn(-1500f, 2500f),
            y = (item.y + deltaY).coerceIn(-1500f, 4000f),
        )
        return true
    }

    /** Check the live allowlist immediately before sending a focus command. */
    fun focusHandle(sourceId: String): String? =
        cards.values.firstOrNull { it.sourceId == sourceId }?.handle
}
