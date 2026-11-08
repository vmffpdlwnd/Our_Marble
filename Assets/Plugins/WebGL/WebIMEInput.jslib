// 웹(WebGL) 빌드에서 한글을 직접 칠 수 있게 하는 브라우저 쪽 코드.
// Unity 입력칸 위에 투명한 HTML <input>을 겹쳐 띄우면, 브라우저가 한글 조합(IME)을 직접 처리한다.
// 글자는 투명하게 숨기고, 입력한 내용은 Unity 입력칸(TMP_InputField)에 실시간으로 넘겨서 Unity가 그린다.
// C# 쪽은 WebIMEInput.cs. (이 파일은 Assets/Plugins/WebGL/ 에 있어야 웹 빌드에만 들어가고 exe에는 안 들어간다)
mergeInto(LibraryManager.library, {

  // 입력칸 위에 HTML 입력칸을 띄운다. stylePtr는 위치와 크기를 담은 JSON (화면 픽셀, 위쪽 기준 y)
  WebIME_Open: function (hostPtr, textPtr, maxLength, stylePtr) {
    var host = UTF8ToString(hostPtr);
    var original = UTF8ToString(textPtr);
    var s = JSON.parse(UTF8ToString(stylePtr));

    var st = window.__webIME = window.__webIME || { input: null, close: null, setText: null };
    if (st.close) st.close(false); // 이미 열려 있으면 알리지 않고 닫는다

    var canvas = (typeof Module !== 'undefined' && Module.canvas) || document.querySelector('canvas');
    if (!canvas) return;

    // Unity 화면 픽셀 -> 브라우저 CSS 픽셀
    var cr = canvas.getBoundingClientRect();
    var kx = cr.width / s.screenW;
    var ky = cr.height / s.screenH;

    var send = function (method, value) {
      if (typeof SendMessage === 'function') SendMessage(host, method, value);
      else if (typeof Module !== 'undefined' && Module.SendMessage) Module.SendMessage(host, method, value);
    };

    var input = document.createElement('input');
    input.type = 'text';
    input.value = original;
    input.autocomplete = 'off';
    input.spellcheck = false;
    input.setAttribute('autocapitalize', 'off');
    if (maxLength > 0) input.maxLength = maxLength;

    var css = input.style;
    css.position = 'fixed';
    css.left = (cr.left + s.x * kx) + 'px';
    css.top = (cr.top + s.y * ky) + 'px';
    css.width = (s.w * kx) + 'px';
    css.height = (s.h * ky) + 'px';
    css.boxSizing = 'border-box';
    css.paddingLeft = (s.padLeft * kx) + 'px';
    css.paddingRight = (s.padRight * kx) + 'px';
    css.margin = '0';
    css.border = '0';
    css.outline = 'none';
    css.background = 'transparent';
    css.color = 'transparent';        // 글자는 Unity가 그린다
    css.caretColor = 'transparent';   // 깜박이는 커서도 Unity 것을 쓴다
    css.fontSize = (s.fontPx * ky) + 'px';
    css.fontFamily = 'sans-serif';
    css.zIndex = '2147483647';

    var openedAt = Date.now();
    var closed = false;

    var finish = function (notify) {
      if (closed) return;
      closed = true;
      var value = input.value;
      st.input = null; st.close = null; st.setText = null;
      if (input.parentNode) input.parentNode.removeChild(input);
      if (canvas.focus) canvas.focus(); // 키 입력을 다시 Unity로 돌려준다
      if (notify) send('OnWebIMEClosed', value);
    };

    input.addEventListener('input', function () { send('OnWebIMEChanged', input.value); });
    input.addEventListener('compositionend', function () { send('OnWebIMEChanged', input.value); });

    input.addEventListener('keydown', function (e) {
      e.stopPropagation(); // 게임의 단축키(Esc 등)로 넘어가지 않게 한다
      if (e.key === 'Enter') {
        if (e.isComposing || e.keyCode === 229) return; // 한글 조합을 확정하는 엔터는 제출이 아니다
        e.preventDefault();
        send('OnWebIMEChanged', input.value);
        send('OnWebIMESubmit', input.value);
      } else if (e.key === 'Escape') {
        e.preventDefault();
        input.value = original; // 취소: 열기 전 글자로 되돌린다
        finish(true);
      } else if (e.key === 'Tab') {
        e.preventDefault();
      }
    });
    input.addEventListener('keyup', function (e) { e.stopPropagation(); });

    input.addEventListener('blur', function () {
      // 열자마자 Unity가 캔버스로 포커스를 가져가는 경우를 막는다
      if (!closed && Date.now() - openedAt < 150) { setTimeout(function () { input.focus(); }, 0); return; }
      finish(true);
    });

    st.input = input;
    st.close = finish;
    st.setText = function (value) { input.value = value; input.focus(); };

    document.body.appendChild(input);
    input.focus();
    try { input.setSelectionRange(input.value.length, input.value.length); } catch (err) { }
  },

  // 열려 있는 HTML 입력칸의 글자를 바꾼다 (채팅을 보낸 뒤 비울 때)
  WebIME_SetText: function (textPtr) {
    var st = window.__webIME;
    if (st && st.setText) st.setText(UTF8ToString(textPtr));
  },

  // 열려 있는 HTML 입력칸을 닫는다 (C#에는 다시 알리지 않는다)
  WebIME_Close: function () {
    var st = window.__webIME;
    if (st && st.close) st.close(false);
  }
});