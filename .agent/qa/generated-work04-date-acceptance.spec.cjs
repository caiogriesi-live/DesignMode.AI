const { test, expect } = require('@playwright/test');
test.describe.configure({ mode: 'serial' });
const claim = 'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name';
const b64 = x => Buffer.from(JSON.stringify(x)).toString('base64url');
const token = `${b64({alg:'none',typ:'JWT'})}.${b64({sub:'fixture',[claim]:'fixture@example.invalid',exp:4102444800})}.`;
const app = 'http://127.0.0.1:5278';
async function setup(browserType, viewport) {
  const browser = await browserType.launch();
  const context = await browser.newContext({ viewport });
  await context.addInitScript(value => {
    localStorage.setItem('authToken', JSON.stringify(value));
    localStorage.setItem('authTokenExpiration', JSON.stringify('2100-01-01T00:00:00Z'));
    window.__dateEvents=[];
    const observe = e => window.__dateEvents.push({type:e.type,targetType:e.target.type,targetAttr:e.target.getAttribute('type'),isTrusted:e.isTrusted});
    document.addEventListener('input',observe,true); document.addEventListener('change',observe,true);
  }, token);
  const rows=[];
  await context.route('**/*', async route => {
    const url=new URL(route.request().url());
    if (url.pathname.includes('/api/business-expenses')) {
      const req=route.request();
      if(req.method()==='GET') {
        const date=url.searchParams.get('date'), currency=url.searchParams.get('currency');
        await route.fulfill({status:200,contentType:'application/json',body:JSON.stringify(rows.filter(x=>x.date===date&&x.currency===currency))});
      } else {
        const body=req.postDataJSON();
        if(req.method()==='POST') { const row={...body,expenseId:'qa-1'}; rows.push(row); await route.fulfill({status:200,contentType:'application/json',body:JSON.stringify(row)}); }
        else if(req.method()==='PUT') { const row={...body,expenseId:'qa-1'}; rows[0]=row; await route.fulfill({status:200,contentType:'application/json',body:JSON.stringify(row)}); }
        else await route.fulfill({status:405,body:'Method not allowed'});
      }
    } else if(url.origin===app) await route.continue();
    else await route.abort();
  });
  const page=await context.newPage();
  const pageErrors=[]; page.on('pageerror',e=>pageErrors.push(e.message));
  await page.goto(`${app}/admin/expenses`);
  await expect(page.getByRole('heading',{name:'Business expenses'})).toBeVisible();
  return {browser,context,page,rows,pageErrors};
}
async function fillRequired(page,description,amount,currency,category) {
  const form=page.getByTestId('expense-form');
  await form.locator('input[type=number]').fill(amount);
  await form.locator('input[maxlength="12"]').fill(currency);
  await form.locator('select').selectOption(category);
  await form.locator('input[maxlength="300"]').fill(description);
}
async function observeDate(input) {
  await input.evaluate(el=>{el.__events=[];for(const type of ['input','change'])el.addEventListener(type,e=>el.__events.push({type,isTrusted:e.isTrusted}));});
  return input.evaluate(el=>el.__events);
}
async function keyboardStep(input,direction) {
  const before=await input.inputValue();
  await input.click({position:{x:42,y:12}}); await input.press(direction); await input.press('Tab');
  const after=await input.inputValue(); const events=await input.evaluate(el=>el.__events);
  return {before,after,events,interaction:`native date field segment; ${direction}; Tab`};
}
async function keyboardDate(input,value) {
  const before=await input.inputValue();
  await input.click(); await input.press('Control+A'); await input.pressSequentially(value); await input.press('Tab');
  const after=await input.inputValue(); const events=await input.evaluate(el=>el.__events);
  return {before,after,events,interaction:`native date field keyboard entry ${value}; Tab`};
}
async function nativeChange(input,engine,value,direction) {
  return engine==='webkit' ? keyboardDate(input,value) : keyboardStep(input,direction);
}

test('Chromium: date fill event probe', async ({ playwright }) => {
  const {browser,context,page,rows,pageErrors}=await setup(playwright.chromium,{width:1440,height:900});
  try {
    const date=page.getByTestId('expense-form').locator('input[type=date]'); await observeDate(date);
    await date.fill('2026-09-17'); const domDate=await date.inputValue(); const events=await date.evaluate(el=>el.__events);
    await fillRequired(page,'Fill-only probe','12.34','USD','software');
    const request=page.waitForRequest(r=>r.method()==='POST'&&r.url().includes('/api/business-expenses'));
    await page.getByTestId('expense-save').click(); const req=await request; const payload=req.postDataJSON();
    expect(pageErrors).toEqual([]); console.log('DATE_FILL_PROBE '+JSON.stringify({engine:'chromium',domDate,events,payloadDate:payload.date,modelUpdated:payload.date===domDate,pageErrors}));
  } finally { await context.close(); await browser.close(); }
});

test('WebKit: date fill event probe', async ({ playwright }) => {
  const {browser,context,page,pageErrors}=await setup(playwright.webkit,{width:390,height:844});
  try {
    const date=page.getByTestId('expense-form').locator('input[type=date]'); await observeDate(date);
    await date.fill('2026-09-17'); const domDate=await date.inputValue(); const events=await date.evaluate(el=>el.__events);
    await fillRequired(page,'Fill-only probe','12.34','USD','software');
    const request=page.waitForRequest(r=>r.method()==='POST'&&r.url().includes('/api/business-expenses'));
    await page.getByTestId('expense-save').click(); const req=await request; const payload=req.postDataJSON();
    expect(pageErrors).toEqual([]); console.log('DATE_FILL_PROBE '+JSON.stringify({engine:'webkit',domDate,events,payloadDate:payload.date,modelUpdated:payload.date===domDate,pageErrors}));
  } finally { await context.close(); await browser.close(); }
});

async function acceptance(browserType,engine,viewport) {
  const {browser,context,page,rows,pageErrors}=await setup(browserType,viewport);
  try {
    const form=page.getByTestId('expense-form'); const date=form.locator('input[type=date]'); await observeDate(date);
    let action=await nativeChange(date,engine,'2026-09-17','ArrowDown');
    expect(action.after,'native date input keyboard selection should change its value').not.toBe(action.before);
    const createdDate=action.after;
    await fillRequired(page,'WORK-04 '+engine,'125.50','USD','software');
    await page.getByTestId('expense-save').click();
    const records=page.getByTestId('expense-records'); console.log('CREATE_TRACE '+JSON.stringify({engine,createdDate,rows,recordsText:await records.innerText()}));
    await expect(records.getByText('WORK-04 '+engine)).toBeVisible();
    expect(rows).toHaveLength(1); expect(rows[0].date).toBe(createdDate);
    expect(rows[0]).toMatchObject({amount:125.5,currency:'USD',category:'software',description:'WORK-04 '+engine});
    const createPayload={...rows[0]};
    await records.getByRole('button',{name:'Edit'}).click();
    const editDate=form.locator('input[type=date]');
    let editAction=await nativeChange(editDate,engine,'2026-09-18','ArrowUp');
    expect(editAction.after,'native date input keyboard edit should change its value').not.toBe(editAction.before);
    const editedDate=editAction.after;
    await fillRequired(page,'WORK-04 '+engine+' edited','150.75','USD','AI/API');
    await page.getByTestId('expense-save').click();
    await expect(records.getByText('WORK-04 '+engine+' edited')).toBeVisible();
    expect(rows).toHaveLength(1); expect(rows[0]).toMatchObject({date:editedDate,amount:150.75,currency:'USD',category:'AI/API',description:'WORK-04 '+engine+' edited'});
    const editPayload={...rows[0]};
    const filters=page.locator('.ledger-filters'); const filterDate=filters.locator('input[type=date]');
    const changed=await nativeChange(filterDate,engine,'2026-09-19','ArrowDown');
    expect(changed.after).not.toBe(changed.before);
    const wrongDateRequest=page.waitForRequest(r=>r.method()==='GET'&&r.url().includes('/api/business-expenses'));
    await filters.getByRole('button',{name:'Show records'}).click();
    const wrongDateUrl=new URL((await wrongDateRequest).url()); expect(wrongDateUrl.searchParams.get('date')).toBe(changed.after); expect(wrongDateUrl.searchParams.get('currency')).toBe('USD');
    await expect(page.getByText('No expenses match this date and currency.')).toBeVisible();
    const restore=await nativeChange(filterDate,engine,'2026-09-18','ArrowUp');
    expect(restore.after).toBe(editedDate);
    const matchingRequest=page.waitForRequest(r=>r.method()==='GET'&&r.url().includes('/api/business-expenses'));
    await filters.getByRole('button',{name:'Show records'}).click();
    const matchingUrl=new URL((await matchingRequest).url()); expect(matchingUrl.searchParams.get('date')).toBe(editedDate); expect(matchingUrl.searchParams.get('currency')).toBe('USD');
    await expect(records.getByText('WORK-04 '+engine+' edited')).toBeVisible();
    await filters.locator('input[maxlength="12"]').fill('EUR');
    const wrongCurrencyRequest=page.waitForRequest(r=>r.method()==='GET'&&r.url().includes('/api/business-expenses'));
    await filters.getByRole('button',{name:'Show records'}).click();
    const wrongCurrencyUrl=new URL((await wrongCurrencyRequest).url()); expect(wrongCurrencyUrl.searchParams.get('date')).toBe(editedDate); expect(wrongCurrencyUrl.searchParams.get('currency')).toBe('EUR');
    await expect(page.getByText('No expenses match this date and currency.')).toBeVisible();
    const overflow=await page.evaluate(()=>document.documentElement.scrollWidth<=document.documentElement.clientWidth);
    expect(overflow).toBe(true); expect(pageErrors).toEqual([]);
    console.log('DATE_ACCEPTANCE '+JSON.stringify({engine,viewport,create:{interaction:action.interaction,events:action.events,payload:createPayload},edit:{interaction:editAction.interaction,events:editAction.events,payload:editPayload},dateFilter:{mismatch:changed.after,restored:editedDate},currencyFilter:'USD matched; EUR empty',overflow}));
  } finally { await context.close(); await browser.close(); }
}
test('Chromium desktop: native date creation, edit, and date/currency filters',async({playwright})=>acceptance(playwright.chromium,'chromium',{width:1440,height:900}));
test('WebKit mobile: native date creation, edit, and date/currency filters',async({playwright})=>acceptance(playwright.webkit,'webkit',{width:390,height:844}));


test('WebKit: native date control sequential keyboard entry', async ({ playwright }) => {
  const {browser,context,page,pageErrors}=await setup(playwright.webkit,{width:390,height:844});
  try {
    const date=page.getByTestId('expense-form').locator('input[type=date]'); await observeDate(date);
    const before=await date.inputValue();
    await date.click(); await date.press('Control+A'); await date.pressSequentially('2026-09-17'); await date.press('Tab');
    let after=await date.inputValue(); let events=await date.evaluate(el=>el.__events);
    let interaction='click, Control+A, type 2026-09-17, Tab';
    if(after===before) {
      await date.click(); await date.press('Control+A'); await date.pressSequentially('09172026'); await date.press('Tab');
      after=await date.inputValue(); events=await date.evaluate(el=>el.__events); interaction='click, Control+A, type 09172026, Tab';
    }
    console.log('WEBKIT_NATIVE_KEYBOARD '+JSON.stringify({before,after,events,pageErrors}));
    expect(after).not.toBe(before); expect(pageErrors).toEqual([]);
  } finally { await context.close(); await browser.close(); }
});

test('WebKit: synthetic change-event binding probe (not native interaction)', async ({ playwright }) => {
  const {browser,context,page,pageErrors}=await setup(playwright.webkit,{width:390,height:844});
  try {
    const date=page.getByTestId('expense-form').locator('input[type=date]'); await observeDate(date);
    await date.fill('2026-09-17');
    await date.evaluate(el=>{el.dispatchEvent(new InputEvent('input',{bubbles:true,inputType:'insertText'}));el.dispatchEvent(new Event('change',{bubbles:true}));});
    const domDate=await date.inputValue(); const events=await date.evaluate(el=>el.__events);
    await fillRequired(page,'Synthetic change probe','12.34','USD','software');
    const request=page.waitForRequest(r=>r.method()==='POST'&&r.url().includes('/api/business-expenses'));
    await page.getByTestId('expense-save').click(); const payload=(await request).postDataJSON();
    expect(payload.date).toBe(domDate); expect(pageErrors).toEqual([]);
    console.log('SYNTHETIC_BINDING_PROBE '+JSON.stringify({engine:'webkit',classification:'synthetic fill plus dispatched input/change; not native interaction',domDate,events,payloadDate:payload.date}));
  } finally { await context.close(); await browser.close(); }
});







