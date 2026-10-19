package com.focuswindow.client

import com.focuswindow.client.workspace.DeskWindow
import com.focuswindow.client.workspace.DeskWorkspace
import org.junit.Assert.*
import org.junit.Test

class DeskWorkspaceTest {
    private fun desk(): DeskWorkspace {
        var n = 0
        return DeskWorkspace { "logical-${++n}" }
    }

    @Test fun refreshRetainsLiveCardsAndLocalLayout() {
        val d = desk()
        val win = DeskWindow("ABCD", "Draft", "Editor")
        d.sync(listOf(win))
        val id = d.snapshot().cards.single().sourceId
        assertTrue(d.addTopic("Research"))
        val topic = d.snapshot().topics.last().id
        assertTrue(d.moveToTopic(id, topic))
        assertTrue(d.move(id, 12f, -8f))
        val before = d.snapshot().cards.single()
        d.sync(listOf(win))
        assertEquals(before, d.snapshot().cards.single())
        assertEquals("ABCD", d.focusHandle(id))
    }

    @Test fun recycledOrMissingWindowCannotBeFocused() {
        val d = desk()
        d.sync(listOf(DeskWindow("A", "First", "Editor")))
        val old = d.snapshot().cards.single().sourceId
        d.sync(listOf(DeskWindow("A", "Second", "Editor")))
        assertNull(d.focusHandle(old))
        assertNotEquals(old, d.snapshot().cards.single().sourceId)
        d.sync(emptyList())
        assertTrue(d.snapshot().cards.isEmpty())
    }

    @Test fun duplicateNamesAndInvalidInput() {
        val d = desk()
        d.sync(listOf(DeskWindow("A", "Same", "Browser"),
            DeskWindow("B", "Same", "Browser"),
            DeskWindow("A", "Same", "Browser"),
            DeskWindow("", "Invalid", "Browser")))
        val cards = d.snapshot().cards
        assertEquals(2, cards.size)
        assertNotEquals(cards[0].sourceId, cards[1].sourceId)
        assertFalse(d.move("missing", 1f, 1f))
        assertFalse(d.move(cards[0].sourceId, Float.NaN, 1f))
        assertFalse(d.moveToTopic(cards[0].sourceId, "missing"))
    }

    @Test fun topicDeletionReturnsCardsToInbox() {
        val d = desk()
        assertFalse(d.addTopic(" "))
        assertFalse(d.addTopic("bad\nname"))
        assertTrue(d.addTopic("Project"))
        assertFalse(d.addTopic("project"))
        d.sync(listOf(DeskWindow("A", "Review", "Editor")))
        val id = d.snapshot().cards.single().sourceId
        val topic = d.snapshot().topics.last().id
        assertTrue(d.moveToTopic(id, topic))
        assertTrue(d.removeTopic(topic))
        assertEquals(DeskWorkspace.INBOX, d.snapshot().cards.single().topicId)
        assertFalse(d.removeTopic(DeskWorkspace.INBOX))
    }

    @Test fun clearedSessionCannotFocusPriorSource() {
        val d = desk()
        d.sync(listOf(DeskWindow("A", "Sensitive title", "Editor")))
        val id = d.snapshot().cards.single().sourceId
        d.clearWindows()
        assertNull(d.focusHandle(id))
    }
}
