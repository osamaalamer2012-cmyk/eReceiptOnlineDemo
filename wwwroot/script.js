const el = (id)=>document.getElementById(id);

// Agent page
if (el('btnIssue')){
  el('btnIssue').onclick = async ()=>{
    const payload = {
      txnId: el('txnId').value,
      msisdn: el('msisdn').value,
      amount: parseFloat(el('amount').value),
      currency: el('currency').value,
      items: [{ sku:'SKU1', name:'Sample Product', qty:1, price: parseFloat(el('amount').value)}]
    };
    const res = await fetch('/tcrm/issue', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(payload) });
    const json = await res.json();
    if(res.ok){
      el('issueResult').innerHTML = '✅ Issued — receiptId: <b>'+json.receiptId+'</b>';
      el('smsPreview').innerHTML = 'SMS to customer: <a href="'+json.shortUrl+'" target="_blank">'+json.shortUrl+'</a>';
    }else{
      el('issueResult').innerText = '❌ '+(json.error || 'Failed');
    }
  };
}

// View page
if (location.pathname.endsWith('/view.html')){
  const params = new URLSearchParams(location.search);
  const token = params.get('token');
  document.getElementById('sendOtp').onclick = async ()=>{
    const res = await fetch('/api/otp/send', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({ token }) });
    const json = await res.json();
    document.getElementById('otpDemo').innerHTML = json.otpDemo ? '<small>DEMO OTP: <b>'+json.otpDemo+'</b></small>' : '';
  };
  document.getElementById('verifyOtp').onclick = async ()=>{
    const code = document.getElementById('otpCode').value;
    const res = await fetch('/api/otp/verify', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({ token, code }) });
    const json = await res.json();
    const status = document.getElementById('status');
    if(res.ok){
      status.innerHTML = '✅ Verified. Opening receipt...';
      window.location.href = '/receipt.html#'+json.receiptId;
    }else{
      status.innerHTML = '❌ '+(json.error || 'Failed');
    }
  };
}

// Receipt page
if (location.pathname.endsWith('/receipt.html')){
  const rid = location.hash.substring(1);
  (async ()=>{
    const res = await fetch('/api/receipt/'+rid);
    const json = await res.json();
    const container = document.getElementById('receiptContainer');
    if(!res.ok){ container.innerHTML = '<section class="card">Not found.</section>'; return; }
    container.className = 'card';
    container.innerHTML = `
      <h2>Receipt #${json.receiptId}</h2>
      <p><b>Txn:</b> ${json.txnId} &nbsp; <b>MSISDN:</b> ${json.msisdn}</p>
      <p><b>Amount:</b> ${json.amount} ${json.currency}</p>
      <table border="0" cellpadding="6"><thead><tr><th align="left">Item</th><th>Qty</th><th>Price</th></tr></thead>
      <tbody>${(json.items||[]).map(i=>`<tr><td>${i.name}</td><td align="center">${i.qty}</td><td align="right">${i.price.toFixed(2)}</td></tr>`).join('')}</tbody></table>
      <p><small>Valid until: ${new Date(json.expiresAt).toLocaleString()}</small></p>
      <button id="downloadPdf">Download PDF (client-side)</button>
    `;
    document.getElementById('downloadPdf').onclick = ()=>window.print();
  })();
}