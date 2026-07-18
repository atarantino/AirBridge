import math
import struct
import unittest

from mic_verify import EXPECTED_FREQUENCY, analyze


class MicrophoneVerifierTests(unittest.TestCase):
    def test_non_bin_centred_reference_tone_is_classified_clean(self):
        sample_rate = 16000
        seconds = 4
        pcm = bytearray()
        for index in range(sample_rate * seconds):
            value = round(2500 * math.sin(2 * math.pi * EXPECTED_FREQUENCY * index / sample_rate))
            pcm.extend(struct.pack("<h", value))

        result = analyze(bytes(pcm), sample_rate, 0, seconds)

        self.assertEqual("clean_tone", result["classification"])
        self.assertAlmostEqual(EXPECTED_FREQUENCY, result["dominant_frequency_hz"], delta=0.5)


if __name__ == "__main__":
    unittest.main()
