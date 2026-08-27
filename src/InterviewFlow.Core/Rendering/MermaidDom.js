// Minimal browser DOM for running mermaid under a .NET JS engine (ADR-001b).
// Prototype-based on purpose: mermaid's bundled DOMPurify reflects over
// Element.prototype / Node.prototype, so accessors must live on prototypes.
// The host may define globalThis.__host_measure_width(text, fontSize, family, bold)
// returning a pixel width; otherwise a crude approximation is used.

(function () {
    'use strict';

    var XHTML_NS = 'http://www.w3.org/1999/xhtml';
    var SVG_NS = 'http://www.w3.org/2000/svg';

    function measureWidth(text, fontSize, family, bold) {
        if (typeof globalThis.__host_measure_width === 'function')
            return globalThis.__host_measure_width(String(text), fontSize, String(family || 'Inter'), !!bold);
        return String(text).length * fontSize * 0.6;
    }

    // ------------------------------------------------------------- Node

    function ShimNode() {
        this._children = [];
        this._parent = null;
        this._doc = null;
    }

    Object.defineProperties(ShimNode.prototype, {
        parentNode: { get: function () { return this._parent; } },
        parentElement: { get: function () { return this._parent && this._parent.nodeType === 1 ? this._parent : null; } },
        childNodes: { get: function () { return this._children; } },
        children: { get: function () { return this._children.filter(function (c) { return c.nodeType === 1; }); } },
        firstChild: { get: function () { return this._children[0] || null; } },
        lastChild: { get: function () { return this._children[this._children.length - 1] || null; } },
        nextSibling: {
            get: function () {
                if (!this._parent) return null;
                var s = this._parent._children;
                var i = s.indexOf(this);
                return i >= 0 && i + 1 < s.length ? s[i + 1] : null;
            }
        },
        previousSibling: {
            get: function () {
                if (!this._parent) return null;
                var s = this._parent._children;
                var i = s.indexOf(this);
                return i > 0 ? s[i - 1] : null;
            }
        },
        ownerDocument: { get: function () { return this._doc; } },
        textContent: {
            get: function () {
                if (this.nodeType === 3 || this.nodeType === 8) return this._text;
                var out = '';
                for (var i = 0; i < this._children.length; i++) out += this._children[i].textContent;
                return out;
            },
            set: function (v) {
                if (this.nodeType === 3 || this.nodeType === 8) { this._text = String(v); return; }
                this._children = [];
                if (v !== null && v !== undefined && v !== '')
                    this.appendChild(this._doc.createTextNode(String(v)));
            }
        },
        isConnected: { get: function () { return true; } }
    });

    ShimNode.prototype.hasChildNodes = function () { return this._children.length > 0; };
    ShimNode.prototype.appendChild = function (c) {
        if (c.nodeType === 11) { // fragment: move children
            while (c._children.length) this.appendChild(c._children[0]);
            return c;
        }
        if (c._parent) c._parent.removeChild(c);
        c._parent = this;
        c._doc = this._doc || c._doc;
        this._children.push(c);
        return c;
    };
    ShimNode.prototype.insertBefore = function (c, ref) {
        if (!ref) return this.appendChild(c);
        if (c._parent) c._parent.removeChild(c);
        var i = this._children.indexOf(ref);
        if (i < 0) return this.appendChild(c);
        c._parent = this;
        c._doc = this._doc || c._doc;
        this._children.splice(i, 0, c);
        return c;
    };
    ShimNode.prototype.removeChild = function (c) {
        var i = this._children.indexOf(c);
        if (i >= 0) { this._children.splice(i, 1); c._parent = null; }
        return c;
    };
    ShimNode.prototype.replaceChild = function (nu, old) {
        var i = this._children.indexOf(old);
        if (i >= 0) { this._children[i] = nu; nu._parent = this; old._parent = null; }
        return old;
    };
    ShimNode.prototype.remove = function () { if (this._parent) this._parent.removeChild(this); };
    ShimNode.prototype.contains = function (n) {
        while (n) { if (n === this) return true; n = n._parent; }
        return false;
    };
    ShimNode.prototype.addEventListener = function () {};
    ShimNode.prototype.removeEventListener = function () {};
    ShimNode.prototype.dispatchEvent = function () { return true; };

    // ------------------------------------------------------------- Text / Comment

    function ShimText(t) {
        ShimNode.call(this);
        this.nodeType = 3;
        this.nodeName = '#text';
        this._text = String(t);
    }
    ShimText.prototype = Object.create(ShimNode.prototype);
    ShimText.prototype.cloneNode = function () { return new ShimText(this._text); };
    Object.defineProperty(ShimText.prototype, 'data', {
        get: function () { return this._text; },
        set: function (v) { this._text = String(v); }
    });

    function ShimComment(t) {
        ShimNode.call(this);
        this.nodeType = 8;
        this.nodeName = '#comment';
        this._text = String(t);
    }
    ShimComment.prototype = Object.create(ShimNode.prototype);
    ShimComment.prototype.cloneNode = function () { return new ShimComment(this._text); };

    // ------------------------------------------------------------- Element

    function ShimElement(tag, ns) {
        ShimNode.call(this);
        this.nodeType = 1;
        this.tagName = tag;
        this.nodeName = tag;
        this.namespaceURI = ns || XHTML_NS;
        this._attrs = [];       // array-like of {name, value, namespaceURI} — DOMPurify iterates numerically
        this._styleMap = {};
        var el = this;
        this.style = {
            setProperty: function (k, v) { el._styleMap[k] = String(v); },
            getPropertyValue: function (k) { return el._styleMap[k] || ''; },
            removeProperty: function (k) { delete el._styleMap[k]; }
        };
        this.classList = {
            _set: {},
            add: function (c) { this._set[c] = true; el._syncClass(); },
            remove: function (c) { delete this._set[c]; el._syncClass(); },
            contains: function (c) { return !!this._set[c]; },
            toggle: function (c) { this._set[c] ? this.remove(c) : this.add(c); }
        };
    }
    ShimElement.prototype = Object.create(ShimNode.prototype);

    ShimElement.prototype._syncClass = function () {
        this.setAttribute('class', Object.keys(this.classList._set).join(' '));
    };

    ShimElement.prototype._findAttr = function (name) {
        for (var i = 0; i < this._attrs.length; i++)
            if (this._attrs[i].name === name) return this._attrs[i];
        return null;
    };
    ShimElement.prototype.setAttribute = function (name, value) {
        var a = this._findAttr(name);
        if (a) a.value = String(value);
        else this._attrs.push({ name: name, value: String(value), namespaceURI: null, ownerElement: this });
        if (name === 'id' && this._doc) this._doc._byId[String(value)] = this;
    };
    ShimElement.prototype.setAttributeNS = function (ns, name, value) { this.setAttribute(name, value); };
    ShimElement.prototype.getAttribute = function (name) {
        var a = this._findAttr(name);
        return a ? a.value : null;
    };
    ShimElement.prototype.getAttributeNS = function (ns, name) { return this.getAttribute(name); };
    ShimElement.prototype.hasAttribute = function (name) { return this._findAttr(name) !== null; };
    ShimElement.prototype.removeAttribute = function (name) {
        for (var i = 0; i < this._attrs.length; i++)
            if (this._attrs[i].name === name) { this._attrs.splice(i, 1); return; }
    };
    ShimElement.prototype.removeAttributeNS = function (ns, name) { this.removeAttribute(name); };
    ShimElement.prototype.getAttributeNode = function (name) { return this._findAttr(name); };
    Object.defineProperty(ShimElement.prototype, 'attributes', {
        get: function () {
            var a = this._attrs;
            a.item = function (i) { return a[i] || null; };
            a.getNamedItem = function (n) {
                for (var i = 0; i < a.length; i++) if (a[i].name === n) return a[i];
                return null;
            };
            return a;
        }
    });

    Object.defineProperty(ShimElement.prototype, 'id', {
        get: function () { return this.getAttribute('id') || ''; },
        set: function (v) { this.setAttribute('id', v); }
    });
    Object.defineProperty(ShimElement.prototype, 'className', {
        get: function () { return this.getAttribute('class') || ''; },
        set: function (v) {
            this.setAttribute('class', v);
            this.classList._set = {};
            var parts = String(v).split(/\s+/);
            for (var i = 0; i < parts.length; i++) if (parts[i]) this.classList._set[parts[i]] = true;
        }
    });

    ShimElement.prototype.cloneNode = function (deep) {
        var copy = new ShimElement(this.tagName, this.namespaceURI);
        copy._doc = this._doc;
        for (var i = 0; i < this._attrs.length; i++)
            copy._attrs.push({ name: this._attrs[i].name, value: this._attrs[i].value, namespaceURI: null, ownerElement: copy });
        for (var k in this._styleMap) copy._styleMap[k] = this._styleMap[k];
        if (deep)
            for (var j = 0; j < this._children.length; j++)
                copy.appendChild(this._children[j].cloneNode(true));
        return copy;
    };

    ShimElement.prototype.matches = function (sel) { return matchesSimple(this, sel); };
    ShimElement.prototype.closest = function (sel) {
        var n = this;
        while (n && n.nodeType === 1) { if (matchesSimple(n, sel)) return n; n = n._parent; }
        return null;
    };
    ShimElement.prototype.querySelector = function (sel) { return queryAll(this, sel, true)[0] || null; };
    ShimElement.prototype.querySelectorAll = function (sel) { return queryAll(this, sel, false); };
    ShimElement.prototype.getElementsByTagName = function (tag) {
        var out = [];
        walk(this, function (n) {
            if (n.nodeType === 1 && (tag === '*' || n.tagName === tag)) out.push(n);
        });
        return out;
    };
    ShimElement.prototype.insertAdjacentHTML = function (position, html) {
        var nodes = parseMarkup(html, this._doc, this.namespaceURI);
        for (var i = 0; i < nodes.length; i++) {
            if (position === 'beforeend') this.appendChild(nodes[i]);
            else if (position === 'afterbegin') this.insertBefore(nodes[i], this.firstChild);
        }
    };
    ShimElement.prototype.focus = function () {};
    ShimElement.prototype.blur = function () {};

    Object.defineProperty(ShimElement.prototype, 'innerHTML', {
        get: function () {
            var s = '';
            for (var i = 0; i < this._children.length; i++) s += serialize(this._children[i]);
            return s;
        },
        set: function (v) {
            this._children = [];
            var nodes = parseMarkup(String(v), this._doc, this.namespaceURI);
            for (var i = 0; i < nodes.length; i++) this.appendChild(nodes[i]);
        }
    });
    Object.defineProperty(ShimElement.prototype, 'outerHTML', {
        get: function () { return serialize(this); }
    });

    // Geometry: approximate from text metrics — good enough to produce valid SVG;
    // exact label layout is refined when the measurement host improves.
    ShimElement.prototype._fontSize = function () {
        var raw = this._styleMap['font-size'] || this.getAttribute('font-size');
        var n = raw ? parseFloat(raw) : NaN;
        return isFinite(n) && n > 0 ? n : 16;
    };
    // Text that actually renders — <style>/<script>/<title>/<desc> content must
    // not influence geometry (the svg root's getBBox would otherwise measure the
    // whole injected stylesheet and blow up the viewBox).
    function collectRenderedText(node, out) {
        if (node.nodeType === 3) { out.push(node._text); return; }
        if (node.nodeType !== 1 && node.nodeType !== 11 && node.nodeType !== 9) return;
        var tag = (node.tagName || '').toLowerCase();
        if (tag === 'style' || tag === 'script' || tag === 'title' || tag === 'desc') return;
        for (var i = 0; i < node._children.length; i++) collectRenderedText(node._children[i], out);
    }

    var SHAPE_TAGS = { rect: 1, circle: 1, ellipse: 1, polygon: 1, polyline: 1 };

    function ownOffset(el) {
        var dx = 0, dy = 0;
        var t = el.getAttribute && el.getAttribute('transform');
        if (t) {
            var m = /translate\(\s*([-\d.]+)[,\s]+([-\d.]+)?\s*\)/.exec(t);
            if (m) { dx += parseFloat(m[1]) || 0; dy += parseFloat(m[2]) || 0; }
        }
        // Shape leaves already fold their x/y attributes into their own bbox;
        // adding them again here would double-count positions.
        if (!SHAPE_TAGS[(el.tagName || '').toLowerCase()]) {
            var x = el.getAttribute && parseFloat(el.getAttribute('x'));
            var y = el.getAttribute && parseFloat(el.getAttribute('y'));
            if (isFinite(x)) dx += x;
            if (isFinite(y)) dy += y;
        }
        return { dx: dx, dy: dy };
    }

    ShimElement.prototype.getBBox = function () {
        var fs = this._fontSize();
        var tag = (this.tagName || '').toLowerCase();

        // Shape leaves report their own geometry attributes. Without this,
        // mermaid's updateNodeBounds reads 0×0 off the shape rect and dagre lays
        // out zero-width nodes — boxes end up overlapping.
        if (tag === 'rect') {
            return {
                x: parseFloat(this.getAttribute('x')) || 0,
                y: parseFloat(this.getAttribute('y')) || 0,
                width: parseFloat(this.getAttribute('width')) || 0,
                height: parseFloat(this.getAttribute('height')) || 0
            };
        }
        if (tag === 'circle') {
            var cr = parseFloat(this.getAttribute('r')) || 0;
            return {
                x: (parseFloat(this.getAttribute('cx')) || 0) - cr,
                y: (parseFloat(this.getAttribute('cy')) || 0) - cr,
                width: cr * 2, height: cr * 2
            };
        }
        if (tag === 'ellipse') {
            var rx = parseFloat(this.getAttribute('rx')) || 0;
            var ry = parseFloat(this.getAttribute('ry')) || 0;
            return {
                x: (parseFloat(this.getAttribute('cx')) || 0) - rx,
                y: (parseFloat(this.getAttribute('cy')) || 0) - ry,
                width: rx * 2, height: ry * 2
            };
        }
        if (tag === 'polygon' || tag === 'polyline') {
            var pts = (this.getAttribute('points') || '').trim().split(/[\s,]+/).map(parseFloat);
            var pminX = Infinity, pminY = Infinity, pmaxX = -Infinity, pmaxY = -Infinity;
            for (var pi = 0; pi + 1 < pts.length; pi += 2) {
                if (!isFinite(pts[pi]) || !isFinite(pts[pi + 1])) continue;
                pminX = Math.min(pminX, pts[pi]); pmaxX = Math.max(pmaxX, pts[pi]);
                pminY = Math.min(pminY, pts[pi + 1]); pmaxY = Math.max(pmaxY, pts[pi + 1]);
            }
            if (!isFinite(pminX)) return { x: 0, y: 0, width: 0, height: 0 };
            return { x: pminX, y: pminY, width: pmaxX - pminX, height: pmaxY - pminY };
        }

        // <text> with tspan children: each direct tspan is one LINE (mermaid emits
        // one outer tspan per line with dy="1.1em"). Measuring the concatenated
        // textContent instead makes multi-line labels absurdly wide and one line
        // tall — dagre then lays boxes out overlapping.
        if (tag === 'text') {
            var lineTspans = [];
            for (var t = 0; t < this._children.length; t++) {
                var ch = this._children[t];
                if (ch.nodeType === 1 && (ch.tagName || '').toLowerCase() === 'tspan')
                    lineTspans.push(ch);
            }
            if (lineTspans.length) {
                var maxW = 0;
                for (var li = 0; li < lineTspans.length; li++) {
                    var lparts = [];
                    collectRenderedText(lineTspans[li], lparts);
                    var lw2 = measureWidth(lparts.join(''), fs, 'Inter', false);
                    if (lw2 > maxW) maxW = lw2;
                }
                return { x: 0, y: 0, width: maxW, height: lineTspans.length * fs * 1.2 };
            }
        }

        // Leaf-ish: direct text content → measure it.
        var hasDirectText = this._children.some(function (c) { return c.nodeType === 3 && c._text.trim(); });
        if (hasDirectText || tag === 'text' || tag === 'tspan' || tag === 'div' || tag === 'span' || tag === 'p') {
            var parts = [];
            collectRenderedText(this, parts);
            var text = parts.join('');
            if (!text) return { x: 0, y: 0, width: 0, height: 0 };
            var lines = text.split('\n');
            var w = 0;
            for (var i = 0; i < lines.length; i++) {
                var lw = measureWidth(lines[i], fs, 'Inter', false);
                if (lw > w) w = lw;
            }
            return { x: 0, y: 0, width: w, height: lines.length * fs * 1.2 };
        }

        // Container: union of children boxes shifted by their translate/x/y, so the
        // svg root's bbox roughly matches the laid-out graph extent.
        var minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
        for (var j = 0; j < this._children.length; j++) {
            var c = this._children[j];
            if (c.nodeType !== 1) continue;
            var ct = (c.tagName || '').toLowerCase();
            if (ct === 'style' || ct === 'script' || ct === 'title' || ct === 'desc') continue;
            var b = c.getBBox();
            if (b.width === 0 && b.height === 0) continue;
            var o = ownOffset(c);
            minX = Math.min(minX, o.dx + b.x);
            minY = Math.min(minY, o.dy + b.y);
            maxX = Math.max(maxX, o.dx + b.x + b.width);
            maxY = Math.max(maxY, o.dy + b.y + b.height);
        }
        if (!isFinite(minX))
            return { x: 0, y: 0, width: 0, height: 0 };
        return { x: minX, y: minY, width: maxX - minX, height: maxY - minY };
    };
    ShimElement.prototype.getBoundingClientRect = function () {
        var b = this.getBBox();
        return { x: b.x, y: b.y, left: b.x, top: b.y, right: b.x + b.width, bottom: b.y + b.height, width: b.width, height: b.height };
    };
    ShimElement.prototype.getComputedTextLength = function () { return this.getBBox().width; };
    ShimElement.prototype.getTotalLength = function () { return 100; };
    ShimElement.prototype.getPointAtLength = function () { return { x: 0, y: 0 }; };

    // ------------------------------------------------------------- selectors

    // Simple selector engine: supports tag, #id, .class, [attr], [attr="v"], and
    // comma lists; combinators (space, >) fall back to matching the last simple
    // selector, which is what d3/mermaid's lookups actually need.
    function matchesSimple(el, selector) {
        var parts = String(selector).split(',');
        for (var p = 0; p < parts.length; p++) {
            var sel = parts[p].trim();
            var segs = sel.split(/[\s>]+/);
            sel = segs[segs.length - 1];
            if (!sel) continue;
            var ok = true;
            var re = /([#.]?[^#.\[\]]+|\[[^\]]+\])/g;
            var m;
            while ((m = re.exec(sel)) !== null) {
                var tok = m[1];
                if (tok.charAt(0) === '#') {
                    if (el.getAttribute('id') !== tok.slice(1)) { ok = false; break; }
                } else if (tok.charAt(0) === '.') {
                    if (!el.classList.contains(tok.slice(1))) { ok = false; break; }
                } else if (tok.charAt(0) === '[') {
                    var body = tok.slice(1, -1);
                    var eq = body.indexOf('=');
                    if (eq < 0) {
                        if (!el.hasAttribute(body)) { ok = false; break; }
                    } else {
                        var an = body.slice(0, eq);
                        var av = body.slice(eq + 1).replace(/^["']|["']$/g, '');
                        if (el.getAttribute(an) !== av) { ok = false; break; }
                    }
                } else if (tok !== '*') {
                    if (el.tagName !== tok) { ok = false; break; }
                }
            }
            if (ok) return true;
        }
        return false;
    }

    function queryAll(root, selector, firstOnly) {
        var out = [];
        // d3's insert(name, ':first-child') resolves the reference node via
        // querySelector — without this, shape rects get appended AFTER labels
        // and paint over the text.
        if (selector === ':first-child' || selector === ':last-child') {
            var kids = root._children;
            if (selector === ':first-child') {
                for (var fc = 0; fc < kids.length; fc++)
                    if (kids[fc].nodeType === 1) { out.push(kids[fc]); break; }
            } else {
                for (var lc = kids.length - 1; lc >= 0; lc--)
                    if (kids[lc].nodeType === 1) { out.push(kids[lc]); break; }
            }
            return out;
        }
        // Fast path: #id / [id="x"] via the document registry.
        var idm = /^#([-\w]+)$/.exec(selector) || /^\[id=["']?([^"'\]]+)["']?\]$/.exec(String(selector).trim());
        if (idm) {
            var doc = root._doc || root;
            var hit = doc._byId[idm[1]];
            if (hit) out.push(hit);
            return out;
        }
        walk(root, function (n) {
            if (firstOnly && out.length) return;
            if (n !== root && n.nodeType === 1 && matchesSimple(n, selector)) out.push(n);
        });
        return out;
    }

    function walk(node, fn) {
        var kids = node._children.slice();
        for (var i = 0; i < kids.length; i++) {
            fn(kids[i]);
            if (kids[i]._children && kids[i]._children.length) walk(kids[i], fn);
        }
    }

    // ------------------------------------------------------------- parse / serialize

    var VOID_TAGS = { br: 1, hr: 1, img: 1, input: 1, meta: 1, link: 1, area: 1, base: 1, col: 1, embed: 1, source: 1, track: 1, wbr: 1 };

    function decodeEntities(s) {
        return String(s)
            .replace(/&#x([0-9a-fA-F]+);/g, function (_, h) { return String.fromCodePoint(parseInt(h, 16)); })
            .replace(/&#(\d+);/g, function (_, d) { return String.fromCodePoint(parseInt(d, 10)); })
            .replace(/&lt;/g, '<').replace(/&gt;/g, '>')
            .replace(/&quot;/g, '"').replace(/&apos;/g, "'")
            .replace(/&nbsp;/g, ' ').replace(/&amp;/g, '&');
    }

    function escapeText(s) {
        return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    }

    function escapeAttr(s) {
        return escapeText(s).replace(/"/g, '&quot;');
    }

    function parseMarkup(input, doc, parentNs) {
        var root = { _children: [], nodeType: 11 };
        root.appendChild = ShimNode.prototype.appendChild;
        root.removeChild = ShimNode.prototype.removeChild;
        root._doc = doc;
        var stack = [{ el: root, ns: parentNs || XHTML_NS }];
        var pos = 0;
        var s = String(input);

        function top() { return stack[stack.length - 1]; }

        while (pos < s.length) {
            var lt = s.indexOf('<', pos);
            if (lt < 0) {
                addText(s.slice(pos));
                break;
            }
            if (lt > pos) addText(s.slice(pos, lt));

            if (s.startsWith('<!--', lt)) {
                var ce = s.indexOf('-->', lt + 4);
                if (ce < 0) break;
                top().el.appendChild(new ShimComment(s.slice(lt + 4, ce)));
                pos = ce + 3;
                continue;
            }
            if (s.startsWith('<!', lt) || s.startsWith('<?', lt)) {
                var de = s.indexOf('>', lt);
                pos = de < 0 ? s.length : de + 1;
                continue;
            }
            var gt = s.indexOf('>', lt);
            if (gt < 0) { addText(s.slice(lt)); break; }
            var tag = s.slice(lt + 1, gt);
            pos = gt + 1;

            if (tag.charAt(0) === '/') {
                var closeName = tag.slice(1).trim();
                for (var i = stack.length - 1; i > 0; i--) {
                    if (stack[i].el.tagName === closeName) { stack.length = i; break; }
                }
                continue;
            }

            var selfClosing = /\/\s*$/.test(tag);
            if (selfClosing) tag = tag.replace(/\/\s*$/, '');
            var nameMatch = /^([^\s/>]+)/.exec(tag);
            if (!nameMatch) continue;
            var name = nameMatch[1];
            var ns = name === 'svg' ? SVG_NS
                : name === 'foreignObject' ? top().ns
                : top().ns;
            var el = new ShimElement(name, name === 'svg' ? SVG_NS : ns);
            el._doc = doc;

            var attrRe = /([^\s=/>"']+)(?:\s*=\s*("([^"]*)"|'([^']*)'|[^\s>]+))?/g;
            var rest = tag.slice(name.length);
            var am;
            while ((am = attrRe.exec(rest)) !== null) {
                var av = am[3] !== undefined ? am[3]
                    : am[4] !== undefined ? am[4]
                    : am[2] !== undefined ? am[2] : '';
                el.setAttribute(am[1], decodeEntities(av));
            }

            top().el.appendChild(el);
            var lower = name.toLowerCase();
            if (!selfClosing && !VOID_TAGS[lower]) {
                // foreignObject children are XHTML again.
                var childNs = name === 'foreignObject' ? XHTML_NS : el.namespaceURI;
                stack.push({ el: el, ns: childNs });
            }
        }

        function addText(t) {
            if (t.length === 0) return;
            var tn = new ShimText(decodeEntities(t));
            tn._doc = doc;
            top().el.appendChild(tn);
        }

        return root._children.slice();
    }

    function serialize(node) {
        if (node.nodeType === 3) return escapeText(node._text);
        if (node.nodeType === 8) return '<!--' + node._text + '-->';
        var s = '<' + node.tagName;
        for (var i = 0; i < node._attrs.length; i++)
            s += ' ' + node._attrs[i].name + '="' + escapeAttr(node._attrs[i].value) + '"';
        var style = '';
        for (var k in node._styleMap) style += k + ':' + node._styleMap[k] + ';';
        if (style) s += ' style="' + escapeAttr(style) + '"';
        // Void elements serialize self-closed: '<br></br>' leaves a stray '</br>'
        // as literal text after mermaid splits labels on '<br>'.
        if (node._children.length === 0 && VOID_TAGS[node.tagName.toLowerCase()])
            return s + '/>';
        s += '>';
        for (var j = 0; j < node._children.length; j++) s += serialize(node._children[j]);
        return s + '</' + node.tagName + '>';
    }

    // ------------------------------------------------------------- Document

    function ShimDocument() {
        ShimNode.call(this);
        this.nodeType = 9;
        this.nodeName = '#document';
        this._byId = {};
        this._doc = this;
        var html = new ShimElement('html');
        var head = new ShimElement('head');
        var body = new ShimElement('body');
        html._doc = head._doc = body._doc = this;
        this.appendChild(html);
        html.appendChild(head);
        html.appendChild(body);
        this.documentElement = html;
        this.head = head;
        this.body = body;
        var doc = this;
        this.implementation = {
            createHTMLDocument: function () { return new ShimDocument(); },
            createDocument: function () { return new ShimDocument(); }
        };
        this.fonts = { ready: Promise.resolve(), check: function () { return true; } };
    }
    ShimDocument.prototype = Object.create(ShimNode.prototype);
    ShimDocument.prototype.createElement = function (tag) {
        var el = new ShimElement(tag, XHTML_NS);
        el._doc = this;
        return el;
    };
    ShimDocument.prototype.createElementNS = function (ns, tag) {
        var el = new ShimElement(tag, ns || SVG_NS);
        el._doc = this;
        return el;
    };
    ShimDocument.prototype.createTextNode = function (t) {
        var n = new ShimText(t);
        n._doc = this;
        return n;
    };
    ShimDocument.prototype.createComment = function (t) {
        var n = new ShimComment(t);
        n._doc = this;
        return n;
    };
    ShimDocument.prototype.createDocumentFragment = function () {
        var f = new ShimNode();
        f.nodeType = 11;
        f.nodeName = '#document-fragment';
        f._doc = this;
        return f;
    };
    ShimDocument.prototype.getElementById = function (id) { return this._byId[id] || null; };
    ShimDocument.prototype.querySelector = function (sel) {
        if (sel === 'body') return this.body;
        return queryAll(this, sel, true)[0] || null;
    };
    ShimDocument.prototype.querySelectorAll = function (sel) { return queryAll(this, sel, false); };
    ShimDocument.prototype.getElementsByTagName = function (tag) { return this.documentElement.getElementsByTagName(tag); };
    ShimDocument.prototype.createNodeIterator = function (root, whatToShow) {
        // Snapshot iteration is enough for DOMPurify's remove-as-you-go sweep.
        var wts = whatToShow === undefined ? 0xFFFFFFFF : whatToShow;
        var list = [];
        walk(root, function (n) {
            var bit = n.nodeType === 1 ? 0x1 : n.nodeType === 3 ? 0x4 : n.nodeType === 8 ? 0x80 : 0;
            if (wts & bit) list.push(n);
        });
        var i = 0;
        return { nextNode: function () { return i < list.length ? list[i++] : null; } };
    };

    // ------------------------------------------------------------- globals

    var document = new ShimDocument();

    globalThis.window = globalThis;
    globalThis.self = globalThis;
    globalThis.document = document;
    globalThis.Node = ShimNode;
    globalThis.Element = ShimElement;
    globalThis.SVGElement = ShimElement;
    globalThis.HTMLElement = ShimElement;
    globalThis.Text = ShimText;
    globalThis.Comment = ShimComment;
    globalThis.Document = ShimDocument;
    globalThis.DocumentFragment = function () {};
    globalThis.HTMLTemplateElement = function () {};
    globalThis.HTMLFormElement = function () {};
    globalThis.HTMLAnchorElement = function () {};
    globalThis.HTMLIFrameElement = function () {};
    globalThis.NamedNodeMap = function () {};
    globalThis.NodeFilter = { SHOW_ALL: 0xFFFFFFFF, SHOW_ELEMENT: 0x1, SHOW_TEXT: 0x4, SHOW_COMMENT: 0x80, SHOW_CDATA_SECTION: 0x8, SHOW_PROCESSING_INSTRUCTION: 0x40 };
    globalThis.DOMParser = function () {
        this.parseFromString = function (str, type) {
            var doc = new ShimDocument();
            var nodes = parseMarkup(str, doc, XHTML_NS);
            for (var i = 0; i < nodes.length; i++) doc.body.appendChild(nodes[i]);
            return doc;
        };
    };

    globalThis.console = globalThis.console || {};
    ['log', 'warn', 'error', 'info', 'debug', 'trace'].forEach(function (m) {
        if (typeof globalThis.console[m] !== 'function') globalThis.console[m] = function () {};
    });
    globalThis.setTimeout = function (fn) { if (typeof fn === 'function') fn(); return 0; };
    globalThis.clearTimeout = function () {};
    // Fire interval callbacks once: code that polls a condition inside a promise
    // would otherwise hang that promise forever (renders come back empty).
    globalThis.setInterval = function (fn) { if (typeof fn === 'function') fn(); return 0; };
    globalThis.clearInterval = function () {};
    globalThis.requestAnimationFrame = function (fn) { if (typeof fn === 'function') fn(0); return 0; };
    globalThis.cancelAnimationFrame = function () {};
    globalThis.queueMicrotask = globalThis.queueMicrotask || function (fn) { Promise.resolve().then(fn); };
    globalThis.requestIdleCallback = function (fn) { if (typeof fn === 'function') fn({ timeRemaining: function () { return 50; }, didTimeout: false }); return 0; };
    globalThis.cancelIdleCallback = function () {};
    globalThis.addEventListener = function () {};
    globalThis.removeEventListener = function () {};
    globalThis.dispatchEvent = function () { return true; };
    globalThis.matchMedia = function () {
        return { matches: false, media: '', addEventListener: function () {}, removeEventListener: function () {}, addListener: function () {}, removeListener: function () {} };
    };
    globalThis.getComputedStyle = function (el) {
        return {
            fontSize: (el && el._fontSize ? el._fontSize() : 16) + 'px',
            getPropertyValue: function (k) {
                if (el && el._styleMap && el._styleMap[k]) return el._styleMap[k];
                if (k === 'font-size') return (el && el._fontSize ? el._fontSize() : 16) + 'px';
                return '';
            }
        };
    };
    globalThis.navigator = { userAgent: 'InterviewFlowShim', language: 'en-US', platform: 'shim' };
    globalThis.location = { href: 'http://localhost/', protocol: 'http:', host: 'localhost' };
    globalThis.performance = globalThis.performance || { now: function () { return Date.now(); } };
    globalThis.structuredClone = globalThis.structuredClone || function (o) { return JSON.parse(JSON.stringify(o)); };
    globalThis.btoa = globalThis.btoa || function (s) {
        var chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
        var out = '';
        for (var i = 0; i < s.length; i += 3) {
            var a = s.charCodeAt(i), b = s.charCodeAt(i + 1), c = s.charCodeAt(i + 2);
            out += chars[a >> 2] + chars[((a & 3) << 4) | (isNaN(b) ? 0 : b >> 4)]
                + (isNaN(b) ? '=' : chars[((b & 15) << 2) | (isNaN(c) ? 0 : c >> 6)])
                + (isNaN(c) ? '=' : chars[c & 63]);
        }
        return out;
    };
})();
