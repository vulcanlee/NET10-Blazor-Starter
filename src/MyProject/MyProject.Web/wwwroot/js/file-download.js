// 由 Blazor Server 觸發的檔案下載。
// 伺服器端以 DotNetStreamReference 把位元組經 SignalR circuit 串流過來，
// 這裡組成 Blob 後以隱藏連結觸發瀏覽器下載。
window.appFileDownload = {
    downloadFromStream: async (fileName, contentStreamReference) => {
        const arrayBuffer = await contentStreamReference.arrayBuffer();
        const url = URL.createObjectURL(new Blob([arrayBuffer], { type: 'text/plain' }));
        const anchor = document.createElement('a');
        anchor.href = url;
        anchor.download = fileName ?? 'download.log';
        document.body.appendChild(anchor);
        anchor.click();
        anchor.remove();
        URL.revokeObjectURL(url);
    }
};
