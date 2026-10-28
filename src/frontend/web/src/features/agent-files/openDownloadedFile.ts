const activeDocumentMediaTypes = new Set([
  'text/html',
  'application/xhtml+xml',
  'image/svg+xml',
  'text/xml',
  'application/xml',
  'application/javascript',
  'text/javascript',
  'application/ecmascript',
]);

export function isActiveDocument(contentType: string, fileName: string): boolean {
  const mediaType = contentType.split(';', 1)[0]?.trim().toLowerCase() ?? '';
  if (activeDocumentMediaTypes.has(mediaType)) {
    return true;
  }

  return /\.(html?|xhtml|svg|xml)$/i.test(fileName);
}

export async function openDownloadedFile(blob: Blob, fileName: string): Promise<void> {
  if (!isActiveDocument(blob.type, fileName)) {
    const url = URL.createObjectURL(blob);
    window.open(url, '_blank', 'noopener');
    return;
  }

  const popup = window.open('', '_blank');
  if (popup === null) {
    return;
  }

  popup.opener = null;
  const frame = popup.document.createElement('iframe');
  frame.setAttribute('sandbox', '');
  frame.style.border = '0';
  frame.style.width = '100%';
  frame.style.height = '100vh';
  frame.srcdoc = await blob.text();
  popup.document.body.style.margin = '0';
  popup.document.body.replaceChildren(frame);
}
