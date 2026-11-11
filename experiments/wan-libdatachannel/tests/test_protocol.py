import pathlib
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from signaling import ROOM


class RoomTests(unittest.TestCase):
    def test_room_validation(self):
        self.assertIsNotNone(ROOM.fullmatch("test-room_22"))
        self.assertIsNone(ROOM.fullmatch("bad room"))
        self.assertIsNone(ROOM.fullmatch(".."))
        self.assertIsNone(ROOM.fullmatch("a" * 65))


if __name__ == "__main__":
    unittest.main()
