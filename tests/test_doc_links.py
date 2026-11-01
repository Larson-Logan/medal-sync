"""Check local Markdown section links using disposable synthetic documents."""
from pathlib import Path
import subprocess
import tempfile
import unittest

CHECKER = Path(__file__).resolve().parents[1] / 'scripts/check-doc-links.mjs'


class DocLinkTests(unittest.TestCase):
    def check_links(self, source, targets=None):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'source.md').write_text(source, encoding='utf-8')
            for name, content in (targets or {}).items():
                path = root / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(content, encoding='utf-8')
            return subprocess.run(['node', str(CHECKER), 'source.md'], cwd=root,
                                  capture_output=True, text=True, encoding='utf-8')

    def test_local_and_same_page_sections(self):
        result = self.check_links('# Here\n[local](#here) [other](target.md#there)\n',
                                  {'target.md': '# There\n'})
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn('2 Markdown anchors checked', result.stdout)

    def test_misspelled_fragments_fail_for_current_and_target_files(self):
        for target in ['#typo', 'target.md#typo']:
            with self.subTest(target=target):
                result = self.check_links(f'# Real\n[broken]({target})\n', {'target.md': '# Real\n'})
                self.assertEqual(1, result.returncode)
                self.assertIn(f'unresolved section anchor {target}', result.stderr)

    def test_formatting_punctuation_unicode_and_setext_headings(self):
        result = self.check_links('''# Hello, `config` &amp; _World_! ###
## [Cloud](https://example.com) **copies**
## Café 中文
## snake_case
## `_literal_` `<tag>` `&amp;`
Setext heading
--------------
[one](#hello-config--world) [two](#cloud-copies)
[three](#caf%C3%A9-%E4%B8%AD%E6%96%87) [four](#snake_case) [five](#setext-heading)
[literal](#_literal_-tag-amp)
''')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn('6 Markdown anchors checked', result.stdout)

    def test_duplicate_headings_avoid_suffix_collisions(self):
        result = self.check_links('''# Repeat
# Repeat
# Repeat-1
# Repeat
[first](#repeat) [second](#repeat-1) [third](#repeat-1-1) [fourth](#repeat-2)
''')
        self.assertEqual(0, result.returncode, result.stderr)

    def test_custom_anchors_do_not_change_heading_numbers(self):
        result = self.check_links('''<a id="repeat"></a><a name='Legacy'></a><a id=bare></a>
# Repeat
# Repeat
[one](#repeat) [two](#repeat-1) [three](#Legacy) [four](#bare)
''')
        self.assertEqual(0, result.returncode, result.stderr)

    def test_code_fences_comments_and_literal_anchors_are_ignored(self):
        for hidden in ['```md\n# Hidden\n```', '~~~~\n# Hidden\n~~~\n~~~~',
                       '```\n<a id="hidden"></a>\n```', '<!--\n# Hidden\n-->',
                       '`<a id="hidden"></a>`', '<a data-id="hidden"></a>',
                       '''<a title='id="hidden"'></a>''']:
            with self.subTest(hidden=hidden):
                result = self.check_links(hidden + '\n[broken](#hidden)\n')
                self.assertEqual(1, result.returncode)
                self.assertIn('unresolved section anchor #hidden', result.stderr)
        result = self.check_links('```\n[ignored](missing.md#missing)\n```\n# Visible\n[ok](#visible)')
        self.assertEqual(0, result.returncode, result.stderr)

    def test_quoted_code_fences_do_not_create_or_renumber_headings(self):
        for marker in ['```', '~~~~']:
            for prefix in ['> ', '> > ']:
                with self.subTest(marker=marker, prefix=prefix):
                    hidden = '\n'.join(prefix + line for line in [marker, '# Hidden', marker])
                    result = self.check_links(hidden + '\n[broken](#hidden)\n')
                    self.assertEqual(1, result.returncode)
                    self.assertIn('unresolved section anchor #hidden', result.stderr)
                    result = self.check_links(hidden + '\n# Hidden\n# Hidden\n[first](#hidden) [second](#hidden-1)\n')
                    self.assertEqual(0, result.returncode, result.stderr)
        result = self.check_links('> ```\n> # Hidden\n\n# Visible\n[ok](#visible)')
        self.assertEqual(0, result.returncode, result.stderr)

    def test_escaped_underscores_remain_literal_heading_text(self):
        result = self.check_links(r'''# \_name\_
# _emphasis_
# `\_code\_`
[literal](#_name_) [emphasis](#emphasis) [code](#_code_)
''')
        self.assertEqual(0, result.returncode, result.stderr)

    def test_html_removal_preserves_inline_heading_word_boundaries(self):
        result = self.check_links('''# hello <em>world</em>
# some<!-- hidden -->thing
[tag](#hello-world) [comment](#something)
''')
        self.assertEqual(0, result.returncode, result.stderr)

    def test_reference_wiki_and_encoded_paths_keep_their_fragment(self):
        result = self.check_links('''[reference][ref]
[ref]: <folder/target%20file.md#some-section>
[[page#another-section|Wiki]]
[root](/folder/target%20file.md#some-section)
''', {'folder/target file.md': '# Some section\n', 'wiki/page.md': '# Another section\n'})
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn('3 Markdown anchors checked', result.stdout)

    def test_missing_files_bad_encoding_and_remote_links(self):
        for target in ['missing.md#section', 'target.md#bad%ZZ']:
            result = self.check_links(f'[broken]({target})')
            self.assertEqual(1, result.returncode)
        result = self.check_links('[remote](https://example.invalid/page#missing)\n[mail](mailto:test@example.invalid)')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn('0 Markdown anchors checked', result.stdout)


if __name__ == '__main__':
    unittest.main()
