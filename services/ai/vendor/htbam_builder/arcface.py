from __future__ import annotations
from pathlib import Path
import json, math
import numpy as np
import pandas as pd
from PIL import Image

import torch
from torch import nn
import torch.nn.functional as F
from torch.utils.data import Dataset, DataLoader
from torchvision import models, transforms
from sklearn.metrics import roc_auc_score, roc_curve, confusion_matrix, precision_recall_fscore_support
import matplotlib.pyplot as plt
import shutil


class ManifestDataset(Dataset):
    def __init__(self, root: str | Path, manifest: pd.DataFrame, transform=None):
        self.root=Path(root); self.df=manifest.reset_index(drop=True); self.transform=transform
    def __len__(self): return len(self.df)
    def __getitem__(self, idx):
        row=self.df.iloc[idx]
        img=Image.open(self.root/row.image_path).convert("RGB")
        if self.transform: img=self.transform(img)
        return img, int(row.class_index)


class EmbeddingBackbone(nn.Module):
    def __init__(self, architecture: str="resnet18", embedding_dim: int=512):
        super().__init__()
        if architecture=="resnet18": net=models.resnet18(weights=None)
        elif architecture=="resnet50": net=models.resnet50(weights=None)
        else: raise ValueError("architecture must be resnet18 or resnet50")
        in_features=net.fc.in_features
        net.fc=nn.Identity()
        self.net=net
        self.proj=nn.Sequential(nn.Linear(in_features,embedding_dim,bias=False),nn.BatchNorm1d(embedding_dim))
    def forward(self,x): return self.proj(self.net(x))


class ArcMarginProduct(nn.Module):
    def __init__(self, in_features: int, out_features: int, scale: float=64.0, margin: float=0.50):
        super().__init__(); self.s=scale; self.m=margin
        self.weight=nn.Parameter(torch.empty(out_features,in_features)); nn.init.xavier_uniform_(self.weight)
        self.cos_m=math.cos(margin); self.sin_m=math.sin(margin)
        self.th=math.cos(math.pi-margin); self.mm=math.sin(math.pi-margin)*margin
    def forward(self, embeddings, labels):
        cosine=F.linear(F.normalize(embeddings),F.normalize(self.weight)).clamp(-1+1e-7,1-1e-7)
        sine=torch.sqrt(torch.clamp(1.0-cosine*cosine,min=0.0))
        phi=cosine*self.cos_m-sine*self.sin_m
        phi=torch.where(cosine>self.th,phi,cosine-self.mm)
        one_hot=torch.zeros_like(cosine); one_hot.scatter_(1,labels.view(-1,1),1.0)
        return (one_hot*phi+(1.0-one_hot)*cosine)*self.s


def train_transform():
    return transforms.Compose([
        transforms.Resize((128,128)), transforms.RandomResizedCrop((112,112),scale=(0.85,1.0)),
        transforms.RandomHorizontalFlip(), transforms.ColorJitter(brightness=0.15,contrast=0.15),
        transforms.ToTensor(), transforms.Normalize([0.5]*3,[0.5]*3)])

def eval_transform():
    return transforms.Compose([transforms.Resize((112,112)),transforms.ToTensor(),transforms.Normalize([0.5]*3,[0.5]*3)])


@torch.no_grad()
def embed_paths(model, root: Path, paths: list[str], device: torch.device, batch_size: int=128) -> dict[str,np.ndarray]:
    class P(Dataset):
        def __init__(self, ps): self.ps=ps; self.tf=eval_transform()
        def __len__(self): return len(self.ps)
        def __getitem__(self,i): return self.tf(Image.open(root/self.ps[i]).convert("RGB")), self.ps[i]
    loader=DataLoader(P(paths),batch_size=batch_size,shuffle=False,num_workers=0)
    model.eval(); out={}
    for imgs,names in loader:
        emb=F.normalize(model(imgs.to(device)),dim=1).cpu().numpy()
        for n,e in zip(names,emb): out[str(n)]=e
    return out


def _verification_scores(model, root: Path, pairs: pd.DataFrame, device: torch.device, batch_size: int=128):
    paths=sorted(set(pairs.image_a.tolist()+pairs.image_b.tolist()))
    embs=embed_paths(model,root,paths,device,batch_size)
    sims=[]; labels=[]
    for _,r in pairs.iterrows():
        sims.append(float(np.dot(embs[r.image_a],embs[r.image_b]))); labels.append(int(r.same))
    return np.asarray(sims,dtype=np.float32), np.asarray(labels,dtype=np.int64)


def verification_metrics(model, root: Path, pairs: pd.DataFrame, device: torch.device, batch_size: int=128) -> dict:
    sims, labels = _verification_scores(model,root,pairs,device,batch_size)
    auc=float(roc_auc_score(labels,sims)) if len(set(labels))>1 else float("nan")
    thresholds=np.linspace(-1,1,2001)
    accs=np.array([np.mean((sims>=t).astype(int)==labels) for t in thresholds])
    idx=int(np.argmax(accs))
    return {"auc":auc,"best_accuracy":float(accs[idx]),"best_threshold":float(thresholds[idx])}


def _save_arcface_reports(model, root: Path, pairs: pd.DataFrame, device: torch.device, out: Path, history: pd.DataFrame, batch_size: int):
    (out/'plots').mkdir(parents=True,exist_ok=True); (out/'reports').mkdir(parents=True,exist_ok=True)
    history.to_csv(out/'history.csv',index=False)

    for col,title,file in [('train_loss','Training Loss','loss_curve.png'),('auc','Validation ROC-AUC','auc_curve.png'),('best_accuracy','Validation Verification Accuracy','verification_accuracy_curve.png')]:
        if col in history.columns:
            fig=plt.figure(figsize=(8,5)); ax=fig.add_subplot(111)
            ax.plot(history['epoch'],history[col]); ax.set_xlabel('Epoch'); ax.set_ylabel(title); ax.set_title(title+' by Epoch'); ax.grid(True,alpha=0.25)
            fig.tight_layout(); fig.savefig(out/'plots'/file,dpi=180); plt.close(fig)

    sims,labels=_verification_scores(model,root,pairs,device,batch_size)
    met=verification_metrics(model,root,pairs,device,batch_size)
    pred=(sims>=float(met['best_threshold'])).astype(int)
    cm=confusion_matrix(labels,pred,labels=[0,1])
    pd.DataFrame(cm,index=['Different','Same'],columns=['Different','Same']).to_csv(out/'reports'/'verification_confusion_matrix.csv')
    p,r,f,support=precision_recall_fscore_support(labels,pred,labels=[0,1],zero_division=0)
    pd.DataFrame({'class':['Different','Same'],'precision':p,'recall':r,'f1':f,'support':support}).to_csv(out/'reports'/'verification_classification_report.csv',index=False)
    pair_out=pairs.copy(); pair_out['similarity']=sims; pair_out['pred_same']=pred; pair_out['correct']=(pred==labels)
    pair_out.to_csv(out/'reports'/'verification_predictions.csv',index=False)

    fig=plt.figure(figsize=(6,5)); ax=fig.add_subplot(111); im=ax.imshow(cm)
    ax.set_xticks([0,1],labels=['Different','Same']); ax.set_yticks([0,1],labels=['Different','Same']); ax.set_xlabel('Predicted'); ax.set_ylabel('True'); ax.set_title('Verification Confusion Matrix')
    for i in range(2):
        for j in range(2): ax.text(j,i,str(int(cm[i,j])),ha='center',va='center')
    fig.colorbar(im,ax=ax); fig.tight_layout(); fig.savefig(out/'plots'/'verification_confusion_matrix.png',dpi=180); plt.close(fig)

    if len(set(labels))>1:
        fpr,tpr,_=roc_curve(labels,sims)
        fig=plt.figure(figsize=(6,5)); ax=fig.add_subplot(111); ax.plot(fpr,tpr,label=f"AUC={met['auc']:.4f}"); ax.plot([0,1],[0,1],linestyle='--'); ax.set_xlabel('False Positive Rate'); ax.set_ylabel('True Positive Rate'); ax.set_title('Verification ROC Curve'); ax.legend(); ax.grid(True,alpha=0.25)
        fig.tight_layout(); fig.savefig(out/'plots'/'roc_curve.png',dpi=180); plt.close(fig)
    with (out/'metrics.json').open('w',encoding='utf-8') as fjson: json.dump(met,fjson,ensure_ascii=False,indent=2)


def train_arcface(root: str | Path, train_manifest: str | Path, val_pairs: str | Path, output_dir: str | Path,
                  architecture="resnet18", embedding_dim=512, epochs=20, batch_size=128, lr=3e-4,
                  num_workers=4, device_name="auto", max_samples: int|None=None):
    root=Path(root); out=Path(output_dir)
    # Retraining replaces previous checkpoint/report files instead of creating v1/v2 folders.
    if out.exists(): shutil.rmtree(out)
    out.mkdir(parents=True,exist_ok=True)
    train_df=pd.read_csv(train_manifest)
    if max_samples:
        train_df=train_df.sample(min(max_samples,len(train_df)),random_state=2026).reset_index(drop=True)
        # A smoke-test subset may omit many original classes, so remap only the classes present.
        present=sorted(map(int,train_df.class_index.unique()))
        remap={old:new for new,old in enumerate(present)}
        train_df["class_index"]=train_df.class_index.map(remap).astype(int)
    if len(train_df) < 2:
        raise ValueError("Need at least 2 training images")
    num_classes=int(train_df.class_index.max())+1
    expected=set(range(num_classes)); actual=set(map(int,train_df.class_index.unique()))
    if actual!=expected: raise ValueError("class_index must be contiguous 0..N-1. Re-run D2 prepare script.")
    pairs=pd.read_csv(val_pairs)
    device=torch.device("cuda" if device_name=="auto" and torch.cuda.is_available() else ("cpu" if device_name=="auto" else device_name))
    ds=ManifestDataset(root,train_df,train_transform())
    effective_batch=max(2,min(int(batch_size),len(ds)))
    drop_last=(len(ds) > effective_batch and len(ds) % effective_batch == 1)
    loader=DataLoader(ds,batch_size=effective_batch,shuffle=True,num_workers=num_workers,pin_memory=device.type=="cuda",drop_last=drop_last)
    model=EmbeddingBackbone(architecture,embedding_dim).to(device); head=ArcMarginProduct(embedding_dim,num_classes).to(device)
    opt=torch.optim.AdamW(list(model.parameters())+list(head.parameters()),lr=lr,weight_decay=1e-4)
    sched=torch.optim.lr_scheduler.CosineAnnealingLR(opt,T_max=max(1,epochs))
    use_amp=device.type=="cuda"; scaler=torch.amp.GradScaler("cuda",enabled=use_amp)
    best=-1.0; history=[]
    for epoch in range(1,epochs+1):
        model.train(); head.train(); running=0.0; n=0
        for imgs,labels in loader:
            imgs,labels=imgs.to(device,non_blocking=True),labels.to(device,non_blocking=True)
            opt.zero_grad(set_to_none=True)
            with torch.amp.autocast(device_type=device.type,enabled=use_amp):
                emb=model(imgs); logits=head(emb,labels); loss=F.cross_entropy(logits,labels)
            scaler.scale(loss).backward(); scaler.step(opt); scaler.update()
            running+=float(loss.item())*imgs.size(0); n+=imgs.size(0)
        sched.step()
        metrics=verification_metrics(model,root,pairs,device,batch_size=min(batch_size,256))
        metrics.update({"epoch":epoch,"train_loss":running/max(n,1),"lr":sched.get_last_lr()[0]})
        history.append(metrics); print(json.dumps(metrics,ensure_ascii=False))
        if metrics["best_accuracy"]>best:
            best=metrics["best_accuracy"]
            torch.save({"model_state":model.state_dict(),"architecture":architecture,"embedding_dim":embedding_dim,
                        "num_classes":num_classes,"verification_threshold":metrics["best_threshold"]},out/"best_encoder.pt")
    history_df=pd.DataFrame(history)
    # Reload best checkpoint before producing final report artifacts.
    best_ckpt=torch.load(out/"best_encoder.pt",map_location=device,weights_only=False)
    model.load_state_dict(best_ckpt["model_state"])
    _save_arcface_reports(model,root,pairs,device,out,history_df,batch_size=min(batch_size,256))
    with (out/"training_config.json").open("w",encoding="utf-8") as f:
        json.dump({"architecture":architecture,"embedding_dim":embedding_dim,"epochs":epochs,"batch_size":batch_size,"lr":lr,"num_classes":num_classes},f,ensure_ascii=False,indent=2)
    return out/"best_encoder.pt"
