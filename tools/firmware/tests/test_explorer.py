import base64
import hashlib
import json
from pathlib import Path
import re
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from blade_firmware.explorer import render_html


class ExplorerTests(unittest.TestCase):
    def test_hostile_text_is_inert_and_assets_are_hash_pinned(self):
        hostile = '</script><img src=x onerror=alert(1)>&'
        html = render_html({'source': {'name': hostile}, 'registers': [{'name': hostile}]})
        self.assertNotIn(hostile, html)
        self.assertIn('\\u003c/script\\u003e', html)
        self.assertNotIn('innerHTML', html)
        self.assertNotRegex(html, r'<(?:script|link)[^>]+(?:src|href)=')
        for tag in ('style', 'script'):
            match = re.search(r'<'+tag+r'>(.*?)</'+tag+r'>', html, re.S)
            digest = base64.b64encode(hashlib.sha256(match.group(1).encode()).digest()).decode()
            self.assertIn('sha256-' + digest, html)
        self.assertIn("default-src 'none'", html)

    def test_empty_unknown_and_large_report_payload(self):
        empty = render_html({'profile': None, 'registers': [], 'thermal_tables': []})
        self.assertIn('Unknown firmware image', empty)
        self.assertIn('No live readings', empty)
        data = {'registers': [{'key': f'b{i // 256}:i{i % 256}', 'address': i} for i in range(2048)]}
        html = render_html(data)
        embedded = re.search(r'<script id="report-data" type="application/json">(.*?)</script>', html, re.S).group(1)
        self.assertEqual(json.loads(embedded), data)
        self.assertIn('pageSize = 30', html)

    def test_detail_thermal_and_provenance_fields_present(self):
        html = render_html({'profile': {'id': 'test'}, 'registers': [], 'thermal_tables': []})
        for label in ('Table offset', 'Bit definitions', 'References', 'Source SHA-256', 'Sensor bounds', 'Manual range', 'targets_rpm', 'evidence_status'):
            self.assertIn(label, html)


if __name__ == '__main__':
    unittest.main()
