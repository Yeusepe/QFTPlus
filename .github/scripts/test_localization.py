import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('localization', Path(__file__).with_name('check-localization.py'))
localization = importlib.util.module_from_spec(spec)
spec.loader.exec_module(localization)


class ResourceChecks(unittest.TestCase):
    def test_android_partial_and_reordered_placeholders(self):
        localization.validate_android(
            {'step': ('Step %1$d: %2$s', True), 'stop': ('Stop', True)},
            {'step': ('%2$s: paso %1$d', True)})

    def test_android_invalid_placeholders(self):
        for translated in [('Step %1$s', True), ('Step %2$d', True), ('Step %d', True),
                           ('Step %1$d', False), ('Step', True), ('', True)]:
            with self.subTest(translated=translated), self.assertRaises(ValueError):
                localization.validate_android({'step': ('Step %1$d', True)}, {'step': translated})

    def test_android_unsafe_resources(self):
        for data in [b'<!DOCTYPE resources><resources/>', b'<resources><style name="theme"/></resources>',
                     b'<resources><string name="start">@string/other</string></resources>',
                     b'<resources><string name="start">A</string><string name="start">B</string></resources>',
                     b'<resources><string name="start" formatted="unknown">A</string></resources>']:
            with self.subTest(data=data), self.assertRaises(ValueError):
                localization.read_android(data)

    def test_partial_translation_and_reordered_placeholders(self):
        localization.validate_translation(
            {'Start': 'Start', 'Step': 'Step {0} of {1}: {2}'},
            {'Step': '{2}: paso {0} de {1}'})

    def test_literal_braces(self):
        self.assertEqual(localization.placeholders('{{literal}} {{{0:0}}}'), ['{0:0}'])
        self.assertEqual(localization.placeholders('{{0}}'), [])

    def test_changed_placeholders(self):
        for value in ['Camara {9}', 'Camara', 'Camara {0}', 'Camara {0:0.00}', 'Camara {0:0', 'Camara {0:0}}']:
            with self.subTest(value=value), self.assertRaises(ValueError):
                localization.validate_translation({'Camera': 'Camera {0:0}'}, {'Camera': value})

    def test_unknown_and_blank_values(self):
        for translated in [{'Unknown': 'Texto'}, {'Start': ' '}]:
            with self.assertRaises(ValueError):
                localization.validate_translation({'Start': 'Start'}, translated)

    def test_unsafe_or_ambiguous_resx(self):
        for data in [
            b'<!DOCTYPE root><root/>',
            b'<root><data name="Start" type="System.Resources.ResXFileRef"><value>secret;System.String</value></data></root>',
            b'<root><data name="Start" mimetype="application/x-microsoft.net.object.binary.base64"><value>payload</value></data></root>',
            b'<root><data name="Start"><value>A</value></data><data name="start"><value>B</value></data></root>',
            b'<root><data name="Start"><value><nested/></value></data></root>',
            b'<root><assembly alias="custom" name="custom"/></root>',
        ]:
            with self.subTest(data=data), self.assertRaises(ValueError):
                localization.read_strings(data)


if __name__ == '__main__':
    unittest.main()
