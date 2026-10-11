"""
Builds tests/Equator.Core.Tests/Data/openstax.tsv: textbook exercises with the book's own answers, used as a
regression corpus for solve, factor and simplify.

Source: the CNXML sources of the OpenStax algebra books, at the last revisions published under CC BY 4.0
(the repositories moved to CC BY-NC-SA 4.0 in March 2026; the earlier revisions keep their license):

    git clone --filter=blob:none --sparse https://github.com/openstax/osbooks-prealgebra-bundle
    git -C osbooks-prealgebra-bundle checkout 77a933c672e9c48c746eb802d588a9a7ddfd6e0d
    git clone --filter=blob:none --sparse https://github.com/openstax/osbooks-college-algebra-bundle
    git -C osbooks-college-algebra-bundle checkout 4922e46ebc04326979e19392ccc2384cb8b9076c
    (sparse-checkout set --no-cone '/modules/*/index.cnxml' '/collections/*' in each)

    python tools/openstax_corpus.py <dir containing both checkouts> tests/Equator.Core.Tests/Data/openstax.tsv

Only exercises that have a worked answer in the book (the odd-numbered ones) and whose instruction is one of the
kinds below are kept; MathML is converted to Equator's input syntax.
"""
import collections
import glob
import io
import os
import re
import sys
import xml.etree.ElementTree as ET

M = '{http://www.w3.org/1998/Math/MathML}'
C = '{http://cnx.rice.edu/cnxml}'
COL = '{http://cnx.rice.edu/collxml}'
MD = '{http://cnx.rice.edu/mdml}'

BOOKS = [
    ('osbooks-prealgebra-bundle', 'prealgebra-2e', 'Prealgebra 2e', 'PA'),
    ('osbooks-prealgebra-bundle', 'elementary-algebra-2e', 'Elementary Algebra 2e', 'EA'),
    ('osbooks-prealgebra-bundle', 'intermediate-algebra-2e', 'Intermediate Algebra 2e', 'IA'),
    ('osbooks-college-algebra-bundle', 'college-algebra-2e', 'College Algebra 2e', 'CA'),
    ('osbooks-college-algebra-bundle', 'algebra-and-trigonometry-2e', 'Algebra and Trigonometry 2e', 'AT'),
]


def usable(kind, problem, answer):
    """Filters out exercises the corpus cannot state: function notation, side conditions, systems of inequalities."""
    if re.search(r'(?<![A-Za-z])[fgh]\*\(', problem + ' ' + answer) or '!=' in problem: return False
    if kind == 'solve':
        if not re.search(r'=|<|>', problem): return False
        if problem.startswith('{') and re.search(r'<|>', problem): return False
        return True
    # An expression to simplify or factor has no relation and no second expression after a comma.
    return not re.search(r'=|<|>', problem) and not top_level_comma(problem)


def top_level_comma(s):
    depth = 0
    for c in s:
        depth += c in '([{'
        depth -= c in ')]}'
        if c == ',' and depth == 0: return True
    return False

# Instruction → kind. Matched against the text after "In/For the following exercises,".
KINDS = [
    (r'^(solve|solve each|solve the|solve using|solve by|use the quadratic formula to solve|use factoring to solve|use the square root property)', 'solve'),
    (r'^(factor|factor completely|factor each|factor the greatest common factor)', 'factor'),
    (r'^(multiply|expand|find the product)', 'expand'),
    (r'^(simplify|divide|add|subtract|add or subtract|perform the indicated|rationalize the denominator)', 'simplify'),
]

FUNCTIONS = {'sin', 'cos', 'tan', 'sec', 'csc', 'cot', 'log', 'ln', 'exp', 'arcsin', 'arccos', 'arctan'}
GREEK = {'π': 'pi', 'θ': 'theta', 'α': 'alpha', 'β': 'beta', 'φ': 'phi', 'λ': 'lambda', 'ω': 'omega'}
OPERATORS = {
    '−': '-', '-': '-', '–': '-', '+': '+', '·': '*', '⋅': '*', '×': '*', '*': '*', '÷': '÷', '/': '/', '=': '=', '<': '<', '>': '>',
    '≤': '<=', '≥': '>=', '≠': '!=', ',': ',', '!': '!', '±': '±', '∓': '∓', '∪': 'U', '^': '^',
    '⁢': '*', '⁡': '', '⁣': ',', ' ': '', ' ': '', '': '', ';': ',', '.': '',
}


class Unsupported(Exception):
    pass


def text(node):
    return (node.text or '').strip()


def tokens(node):
    """MathML element → list of (kind, text): kind is operand, op, open, close, func, word."""
    tag = node.tag.replace(M, '')
    kids = list(node)
    if tag in ('math', 'mrow', 'mstyle', 'mpadded', 'mphantom', 'semantics', 'menclose'):
        if tag == 'mphantom': return []
        out = []
        for k in kids:
            if k.tag.replace(M, '') in ('annotation', 'annotation-xml'): continue
            out += tokens(k)
        # A system is written with an opening brace only: { followed by the table of equations.
        if len(out) == 2 and out[0] == ('open', '{') and out[1][1].startswith('{'): return out[1:]
        return out
    if tag == 'mi':
        t = text(node)
        if t in FUNCTIONS: return [('func', 'log' if t == 'ln' else ('log10' if t == 'log' else t))]
        if t in GREEK: return [('operand', GREEK[t])]
        if t == '∞': return [('operand', 'oo')]
        if t == 'e': return [('operand', 'e')]
        if t == 'i': return [('operand', 'I')]
        if not t: return []
        if t in ('−', '-'): return [('op', '-')]
        if not re.fullmatch(r'[A-Za-z]+', t): raise Unsupported('mi ' + t)
        return [('operand', c) for c in t]
    if tag == 'mn':
        t = text(node).replace(',', '').replace(' ', '').replace(' ', '')
        negative = t[:1] in ('−', '-')
        if negative: t = t[1:]
        if not re.fullmatch(r'\d*\.?\d+', t): raise Unsupported('mn ' + t)
        return ([('op', '-')] if negative else []) + [('operand', t)]
    if tag == 'mo':
        t = text(node)
        if t in ('(', '[', '{'): return [('open', t)]
        if t in (')', ']', '}'): return [('close', t)]
        if t in ('|', '│'): return [('bar', '|')]
        if t == '∞': return [('operand', 'oo')]
        if t in OPERATORS: return [('op', OPERATORS[t])] if OPERATORS[t] else []
        raise Unsupported('mo ' + t)
    if tag == 'mtext':
        t = text(node).replace(' ', ' ').strip()
        if t in ('−', '-', '–'): return [('op', '-')]
        if t in ('log', 'ln'): return [('func', 'log' if t == 'ln' else 'log10')]
        if t in (',', ';'): return [('op', ',')]
        return [('word', t)] if t else []
    if tag == 'mtable':
        # A system of equations, one per row.
        rows = []
        for tr in kids:
            cells = []
            for td in tr:
                for k in td: cells += tokens(k)
            if cells: rows.append(join(cells))
        if len(rows) < 2 or not all(re.search(r'=|<|>', r) for r in rows): raise Unsupported('mtable')
        return [('operand', '{' + ', '.join(rows) + '}')]
    if tag == 'mspace':
        return []
    if tag == 'mfenced':
        o, c = node.get('open', '('), node.get('close', ')')
        inner = []
        for i, k in enumerate(kids):
            if i: inner.append(('op', ','))
            inner += tokens(k)
        if o == '|' and c == '|': return [('operand', 'abs(' + join(inner) + ')')]
        return [('open', o)] + inner + [('close', c)]
    if tag == 'msup':
        base, exp = kids
        b = tokens(base)
        if len(b) == 1 and b[0][0] == 'func': raise Unsupported('function power')
        e = paren(join(tokens(exp)))
        # (3x − 2)² is sometimes written with the exponent on the closing bracket alone.
        if b and b[-1][0] == 'close' and not any(k == 'open' for k, _ in b):
            return b + [('op', '^'), ('operand', e)]
        return [('operand', group(b) + '^' + e)]
    if tag == 'msub':
        base, sub = kids
        b = tokens(base)
        if b == [('func', 'log10')]: return [('func', 'log_' + join(tokens(sub)))]
        s = join(tokens(sub))
        if len(b) == 1 and b[0][0] == 'operand' and re.fullmatch(r'[a-zA-Z]', b[0][1]) and re.fullmatch(r'\d', s):
            return [('operand', b[0][1] + s)]
        raise Unsupported('msub')
    if tag == 'mfrac':
        n, d = kids
        return [('operand', '(' + paren(join(tokens(n))) + '/' + paren(join(tokens(d))) + ')')]
    if tag == 'msqrt':
        inner = []
        for k in kids: inner += tokens(k)
        return [('operand', 'sqrt(' + join(inner) + ')')]
    if tag == 'mroot':
        x, n = kids
        return [('operand', 'root(' + join(tokens(x)) + ', ' + join(tokens(n)) + ')')]
    raise Unsupported(tag)


def paren(s):
    return s if re.fullmatch(r'[A-Za-z0-9.]+', s) else '(' + s + ')'


def wrapped(s):
    """Whether s is one parenthesised group, (…)."""
    if not (s.startswith('(') and s.endswith(')')): return False
    depth = 0
    for i, c in enumerate(s):
        depth += c == '('
        depth -= c == ')'
        if depth == 0 and i < len(s) - 1: return False
    return True


def group(toks):
    s = join(toks)
    if wrapped(s): return s
    # A name, a number or a single function call f(…) needs no brackets as a base.
    if re.fullmatch(r'[A-Za-z0-9.]+', s): return s
    call = re.match(r'[a-z]+(?=\()', s)
    if call and wrapped(s[call.end():]): return s
    return '(' + s + ')'


def join(toks):
    """Joins tokens, inserting * for implicit products, turning |...| into abs(...) and f x into f(x)."""
    out = []
    prev = None  # kind of the previous emitted item: 'operand', 'close', 'op', 'open', 'func'
    i = 0
    bars = 0
    keep = []  # per open bracket: whether it is written as is (an interval or a pair such as [2, 5))
    while i < len(toks):
        kind, t = toks[i]
        if kind == 'bar':
            # Opening bar where an operand may start, closing bar after an operand.
            if prev in (None, 'op', 'open', 'func') or bars == 0:
                if prev in ('operand', 'close'): out.append('*')
                out.append('abs('); bars += 1; prev = 'open'
            else:
                out.append(')'); bars -= 1; prev = 'close'
            i += 1
            continue
        if kind == 'word':
            raise Unsupported('word ' + t)
        # A mixed number: 2 followed by 1/3 is 2 + 1/3, not a product.
        if kind == 'operand' and prev == 'operand' and re.fullmatch(r'\d+', out[-1]) and re.fullmatch(r'\(\d+/\d+\)', t):
            out[-1] = '(' + out[-1] + ' + ' + t[1:-1] + ')'
            i += 1
            continue
        # A number split over two <mn> elements (1, 2 for 12).
        if kind == 'operand' and prev == 'operand' and re.fullmatch(r'\d+', out[-1]) and re.fullmatch(r'\d+(\.\d+)?', t):
            out[-1] += t
            i += 1
            continue
        # a ÷ 3x divides by the whole product 3x.
        if kind == 'op' and t == '÷':
            j = i + 1
            depth = 0
            while j < len(toks):
                k2, t2 = toks[j]
                if k2 == 'open': depth += 1
                elif k2 == 'close':
                    if depth == 0: break
                    depth -= 1
                elif k2 == 'op' and depth == 0 and t2 != '^': break
                j += 1
            out.append('/(' + join(toks[i + 1:j]) + ')')
            prev = 'operand'
            i = j
            continue
        if kind in ('operand', 'open', 'func') and prev in ('operand', 'close'):
            out.append('*')
        if kind == 'func':
            name = t
            nxt = toks[i + 1] if i + 1 < len(toks) else None
            if nxt and nxt[0] == 'open':
                # f(...): copy the bracketed group.
                depth, j = 0, i + 1
                while j < len(toks):
                    if toks[j][0] == 'open': depth += 1
                    elif toks[j][0] == 'close':
                        depth -= 1
                        if depth == 0: break
                    j += 1
                arg = join(toks[i + 2:j])
                i = j + 1
            elif nxt and nxt[0] == 'operand':
                # log 4x is log(4x): the argument is the whole implicit product.
                j = i + 1
                while j < len(toks) and toks[j][0] == 'operand': j += 1
                arg = join(toks[i + 1:j]); i = j
            else:
                raise Unsupported('function without argument')
            if name.startswith('log_'): out.append(f'log({arg}, {name[4:]})')
            else: out.append(f'{name}({arg})')
            prev = 'operand'
            continue
        if kind == 'open':
            # Brackets around a comma list ((2, 5], (-oo, 3)) are kept as written; others are grouping.
            depth, j, comma = 0, i, False
            while j < len(toks):
                if toks[j][0] == 'open': depth += 1
                elif toks[j][0] == 'close':
                    depth -= 1
                    if depth == 0: break
                elif toks[j] == ('op', ',') and depth == 1: comma = True
                j += 1
            keep.append(comma)
            out.append(t if comma else '(')
        elif kind == 'close':
            kept = keep.pop() if keep else False
            out.append(t if kept else ')')
        elif kind == 'op': out.append(f' {t} ' if t in ('=', '<', '>', '<=', '>=', '!=', '+', '-', '±', '∓', 'U') else (', ' if t == ',' else t))
        else: out.append(t)
        prev = {'op': 'op', 'open': 'open', 'close': 'close'}.get(kind, 'operand')
        i += 1
    if bars: raise Unsupported('unbalanced bars')
    s = ''.join(out)
    s = re.sub(r'([(\[]\s*)([+-])\s*', r'\1\2', s)
    s = re.sub(r'([(\[])\s+', r'\1', s)
    s = re.sub(r'^\s*([+-])\s+', r'\1', s)
    s = re.sub(r'([=<>,]\s*)([+-])\s+', r'\1\2', s)
    return re.sub(r'\s+', ' ', s).strip()


# Words allowed between the formulas of an answer, and how they are written in the corpus.
ANSWER_WORDS = {
    'or': 'or', 'and': 'and', ',': ',', ';': ',', 'no solution': 'no solution', 'no real solution': 'no solution',
    'prime': 'prime', 'all real numbers': 'all real numbers', 'infinitely many solutions': 'infinitely many solutions',
    'inconsistent': 'no solution', 'no solution, inconsistent': 'no solution', 'inconsistent, no solution': 'no solution',
}


def para_text(para, answer):
    """
    A paragraph → one string. A problem must be formulas only (word problems are skipped); an answer may join its
    formulas with a few words such as "or".
    """
    parts = []
    def words(t):
        t = ' '.join((t or '').split()).strip(' .')
        if not t: return
        key = t.lower()
        if not answer or key not in ANSWER_WORDS: raise Unsupported('para text')
        parts.append(ANSWER_WORDS[key])
    words(para.text)
    for child in para:
        if child.tag == M + 'math':
            formula = join(tokens(child))
            # Drop the brackets a fraction brings when it is the whole formula; never those of an interval or pair.
            inner = formula[1:-1]
            plain = wrapped(formula) and '[' not in formula and ']' not in formula and not top_level_comma(inner)
            parts.append(inner if plain else formula)
        elif child.tag == C + 'newline':
            pass
        else:
            raise Unsupported('para child ' + child.tag)
        words(child.tail)
    return ' '.join(p for p in parts if p).replace(' , ', ', ')


def instruction_kind(s):
    m = re.match(r'(?:In|For) the following exercises?,\s*(.*)', s, re.I)
    if not m: return None
    rest = m.group(1).strip().lower()
    for pattern, kind in KINDS:
        if re.match(pattern, rest): return kind
    return 'other'


def module_titles(bundle, collection):
    root = ET.parse(os.path.join(bundle, 'collections', collection + '.collection.xml')).getroot()
    order, chapter = [], None
    def walk(node, depth):
        nonlocal chapter
        for child in node:
            if child.tag == COL + 'subcollection':
                title = child.find(MD + 'title')
                if depth == 0: chapter = title.text if title is not None else None
                walk(child.find(COL + 'content'), depth + 1)
            elif child.tag == COL + 'module':
                order.append((child.get('document'), chapter))
    walk(root.find(COL + 'content'), 0)
    return order


def harvest(src_dir):
    rows, stats, seen = [], collections.Counter(), set()
    for bundle_name, collection, book, code in BOOKS:
        bundle = os.path.join(src_dir, bundle_name)
        for module, chapter in module_titles(bundle, collection):
            path = os.path.join(bundle, 'modules', module, 'index.cnxml')
            root = ET.parse(path).getroot()
            title = root.find(C + 'title')
            section = title.text.strip() if title is not None and title.text else module
            kind = None
            for node in root.iter():
                if node.tag == C + 'para' and node.text and re.match(r'\s*(In|For) the following exercises?', node.text):
                    kind = instruction_kind(' '.join(''.join(node.itertext()).split()))
                if node.tag != C + 'exercise' or kind in (None, 'other'): continue
                problem, solution = node.find(C + 'problem'), node.find(C + 'solution')
                if problem is None or solution is None: continue
                pp, sp = problem.findall(C + 'para'), solution.findall(C + 'para')
                if len(pp) != 1 or len(sp) != 1: stats['multi-paragraph'] += 1; continue
                try:
                    p, s = para_text(pp[0], answer=False), para_text(sp[0], answer=True)
                except (Unsupported, ValueError) as e:
                    stats['unsupported: ' + str(e).split(' ')[0]] += 1
                    continue
                if not p or not s or re.search(r'[ⓐⓑⓒ]', p + s): stats['empty or parts'] += 1; continue
                if '\t' in p + s: continue
                if not usable(kind, p, s): stats['filtered'] += 1; continue
                # The books share sections: keep each exercise once.
                if (kind, p, s) in seen: stats['duplicate'] += 1; continue
                seen.add((kind, p, s))
                rows.append((book, chapter or '', section, code + '/' + node.get('id'), kind, p, s))
                stats['kept ' + kind] += 1
    return rows, stats


def main():
    src_dir, out = sys.argv[1], sys.argv[2]
    rows, stats = harvest(src_dir)
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with io.open(out, 'w', encoding='utf-8', newline='\n') as f:
        f.write('# Exercises and answers from OpenStax textbooks (https://openstax.org): Prealgebra 2e, Elementary Algebra 2e,\n')
        f.write('# Intermediate Algebra 2e (Lynn Marecek, MaryAnne Anthony-Smith, Andrea Honeycutt Mathis), College Algebra 2e and\n')
        f.write('# Algebra and Trigonometry 2e (Jay Abramson). © Rice University, licensed under CC BY 4.0\n')
        f.write('# (https://creativecommons.org/licenses/by/4.0/). Access for free at openstax.org. Formulas converted from MathML to\n')
        f.write('# Equator syntax by tools/openstax_corpus.py; see that script for the source revisions.\n')
        f.write('# book\tchapter\tsection\tid\tkind\tproblem\tanswer\n')
        for r in rows: f.write('\t'.join(r) + '\n')
    for k, v in sorted(stats.items()): print(f'{v:6} {k}')


if __name__ == '__main__':
    main()
