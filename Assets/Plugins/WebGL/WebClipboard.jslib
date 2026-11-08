// 웹(WebGL) 빌드에서 글자를 클립보드에 복사하는 브라우저 쪽 코드.
// Unity 웹 빌드는 시스템 클립보드를 지원하지 않아서(GUIUtility.systemCopyBuffer가 안 먹힘), 브라우저에 직접 복사를 시킨다.
// 1순위는 보이지 않는 textarea를 만들어 선택한 뒤 execCommand('copy')를 하는 방식이다.
//   - 그 자리에서 성공 여부를 알 수 있고, itch.io처럼 iframe 안에서도 막히지 않는다.
// 이게 실패하면 navigator.clipboard.writeText를 시도하지만, 결과를 기다릴 수 없어서 성공으로 치지 않는다.
// C# 쪽은 ClipboardCopy.cs. (Assets/Plugins/WebGL/ 에 있어야 웹 빌드에만 들어가고 exe에는 안 들어간다)
mergeInto(LibraryManager.library, {

  // 복사에 성공하면 1, 아니면 0
  WebClipboard_Copy: function (textPtr) {
    var text = UTF8ToString(textPtr);
    var ok = false;
    var area = null;

    try {
      area = document.createElement('textarea');
      area.value = text;
      area.setAttribute('readonly', ''); // 모바일에서 키보드가 올라오지 않게 한다
      var css = area.style;
      css.position = 'fixed';
      css.top = '0';
      css.left = '0';
      css.width = '1px';
      css.height = '1px';
      css.padding = '0';
      css.border = '0';
      css.outline = 'none';
      css.opacity = '0';
      document.body.appendChild(area);
      area.focus();
      area.select();
      area.setSelectionRange(0, text.length);
      ok = document.execCommand('copy');
    } catch (err) {
      ok = false;
    } finally {
      if (area && area.parentNode) area.parentNode.removeChild(area);
    }

    // 키 입력을 다시 Unity로 돌려준다
    var canvas = (typeof Module !== 'undefined' && Module.canvas) || document.querySelector('canvas');
    if (canvas && canvas.focus) canvas.focus();

    if (!ok && navigator.clipboard && navigator.clipboard.writeText) {
      try { navigator.clipboard.writeText(text).catch(function () { }); } catch (err) { }
    }
    return ok ? 1 : 0;
  }
});