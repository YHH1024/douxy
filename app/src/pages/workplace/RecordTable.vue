<template>
  <div>
    <!-- 浼樺寲鏌ヨ鍖哄煙锛氳皟鏁村竷灞€锛屾椂闂淬€佸崥涓诲悕绉般€佹爣棰樻斁涓€琛岋紝瀹藉害鑷€傚簲 -->
    <div class="query-container">
      <a-form layout="inline" :model="quaryData" class="query-form">

        <!-- 绗竴琛岋細鏃堕棿閫夋嫨鍣ㄧ粍 + 鍗氫富鍚嶇О + 鏍囬锛堝悎骞朵负涓€琛岋紝鑷€傚簲瀹藉害锛?-->
        <div class="form-row form-main-row">

          <a-form-item label="鍚屾鏃ユ湡" class="form-item form-item-date">
            <a-range-picker v-model:value="value1" :ranges="ranges" :locale="locale" @change="datePicked" class="range-picker" />
          </a-form-item>

          <a-form-item label="鍙戝竷鏃ユ湡" class="form-item form-item-date">
            <a-range-picker v-model:value="value2" :ranges="ranges2" :locale="locale" @change="datePicked2" class="range-picker" />
          </a-form-item>

          <a-form-item label="鍗氫富" ref="author" name="author" class="form-item form-item-input">
            <a-input v-model:value="quaryData.author" class="query-input" placeholder="璇疯緭鍏ュ崥涓诲悕绉? />
          </a-form-item>
          <a-form-item label="鏍囬" ref="title" name="title" class="form-item form-item-input">
            <a-input v-model:value="quaryData.title" class="query-input" placeholder="璇疯緭鍏ユ爣棰? />
          </a-form-item>
        </div>

        <!-- 绗簩琛岋細鍗曢€夌粍 + 鎸夐挳缁?-->
        <div class="form-row form-actions-row">
          <a-form-item class="form-item">
            <a-select ref="select" v-model:value="quaryData.cookieId" style="width: 120px" :options="cookies"></a-select>
          </a-form-item>
          <a-form-item label="瑙嗛绫诲瀷" class="form-item radio-group-item">
            <a-radio-group v-model:value="quaryData.viedoType" button-style="solid" @change="onViedoTypeChanged" class="video-type-radio">
              <a-radio-button value="*">鍏ㄩ儴</a-radio-button>
              <a-radio-button value="1">鍠滄鐨?/a-radio-button>
              <a-radio-button value="2">鏀惰棌鐨?/a-radio-button>
              <a-radio-button value="3">鍏虫敞鐨?/a-radio-button>
              <a-radio-button value="4" v-if="showImageViedo">鍥炬枃瑙嗛</a-radio-button>
              <a-radio-button value="5">鏀惰棌澶?/a-radio-button>
              <a-radio-button value="6">鍚堥泦</a-radio-button>
              <a-radio-button value="7">鐭墽</a-radio-button>
            </a-radio-group>
          </a-form-item>

          <a-button type="primary" @click="GetRecords" class="query-button">
            <SearchOutlined />鏌ヨ
          </a-button>
          <a-form-item class="form-item batch-operation-item" style="margin-left:20px;">
            <a-switch v-model:checked="isBatchMode" checked-children="鎵归噺" un-checked-children="鎵归噺" class="batch-switch" />
          </a-form-item>

          <a-form-item class="form-item button-group-item">
            <a-space size="middle" class="button-group">
              <!-- <a-button success @click="handleBatchShare" class="delete-button" v-if="isBatchMode" :disabled="selectedRowKeys.length === 0 || isSyncing">
                <ShareAltOutlined />
                鎵归噺鍒嗕韩
              </a-button> -->
              <a-button danger @click="handleBatchSync" class="delete-button" v-if="isBatchMode" :disabled="selectedRowKeys.length === 0 || isSyncing">
                <SyncOutlined />
                閲嶆柊涓嬭浇
              </a-button>
              <a-button type="primary" @click="handleBatchAsr" class="delete-button" v-if="isBatchMode" :disabled="selectedRowKeys.length === 0 || isSyncing">
                鐢熸垚瀛楀箷
              </a-button>
              <a-button danger @click="handleBatchDelete" class="delete-button" v-if="isBatchMode" :disabled="selectedRowKeys.length === 0 || isSyncing">
                <close-outlined />
                姘镐箙鍒犻櫎
              </a-button>
            </a-space>
          </a-form-item>
          <!-- 鎸夐挳浠ｇ爜 -->
          <a-form-item class="form-item delete-btn-2-wrapper">
            <a-button type="primary" danger @click="handShowDeleteVideos" class="delete-button-2">
              <!-- <ClearOutlined />  -->
              <!-- 娉ㄦ剰棣栧瓧姣嶅ぇ鍐欙紝Antd鍥炬爣鍛藉悕瑙勮寖 -->
              <delete-outlined />
              宸插垹闄?
            </a-button>
          </a-form-item>
        </div>
      </a-form>
    </div>

    <!-- 宸插垹闄よ棰?鎶藉眽 -->

    <a-drawer title="宸插垹闄よ棰? size="large" :visible="deleteVideoShow" @close="onDeleteVideoClose">
      <template #extra>
      </template>
      <a-list size="small" bordered :data-source="deleteVideos">
        <template #renderItem="{item, index}">
          <a-list-item>
            <!-- 鏂板鏂囨湰瀹瑰櫒锛岀敤浜庢帶鍒剁渷鐣ュ彿 -->
            <div class="delete-video-title-container">
              <span class="delete-video-index">{{ index + 1 }}.</span>
              <span class="delete-video-title" :title="item.videoTitle || '鏃犳爣棰?">
                {{ item.videoTitle }}
              </span>
            </div>

            <!-- <a-button type="text" size="small" class="copy-delete-video-btn" @click="(e) => copyVideoPath(item.videoSavePath)">
              <CopyOutlined /> 澶嶅埗
            </a-button> -->
          </a-list-item>
        </template>
      </a-list>
    </a-drawer>
    <!-- 瑙嗛鎾斁寮圭獥 - 淇濇寔鍘熸湁 -->
    <a-modal v-model:visible="isModalOpen" :width="900" :mask-closable="false" :footer="null" @cancel="handleCancel" :body-style="{ padding: '0', overflow: 'hidden', backgroundColor: '#fff' }" :style="{ 
    borderRadius: '8px',
    maxWidth: '85vw',
    maxHeight: '80vh',
    minWidth: '500px',
    minHeight: '400px'
  }" :mask-style="{ backgroundColor: 'rgba(0, 0, 0, 0.5)' }">
      <!-- 鑷畾涔夊脊绐楁爣棰橈紙鏇夸唬鍘熸潵鐨?title灞炴€э級 -->
      <template #title>
        <span class="modal-title-with-tooltip" :title="formatFilePath(currentVideoInfo?.videoSavePath)">
          {{ playingTitle }}
        </span>
      </template>
      <div class="video-container">
        <div v-if="isVideoLoading" class="loading-overlay">
          <a-spin size="large" tip="瑙嗛鍔犺浇涓?.." />
          <p class="loading-tip">璇风◢鍊欙紝姝ｅ湪涓烘偍鍑嗗瑙嗛...</p>
        </div>
        <div v-else-if="hasError" class="error-container">
          <a-alert type="error" showIcon :message="errorMessage" description="寤鸿灏濊瘯锛?. 妫€鏌ョ綉缁滆繛鎺?2. 鍒锋柊椤甸潰閲嶈瘯 3. 鑱旂郴绠＄悊鍛? />
        </div>
        <video ref="videoRef" class="video-element" controls preload="metadata" :autoplay="autoPlay" :muted="autoMuted" @error="handleVideoError" @loadeddata="() => isVideoLoading = false" @waiting="() => isVideoLoading = true" @canplay="() => isVideoLoading = false" :style="{ opacity: isVideoLoading || hasError ? 0 : 1, transition: 'opacity 0.3s ease' }">
          <source :src="videoUrl" type="video/mp4" />
          鎮ㄧ殑娴忚鍣ㄤ笉鏀寔 HTML5 瑙嗛鎾斁锛岃鍗囩骇娴忚鍣ㄣ€?
        </video>
      </div>
      <div v-if="currentVideoInfo" class="video-info-bar">
        <div class="info-container">
          <div class="info-item">
            <span class="info-label">鍚屾鏃堕棿锛?/span>
            <span class="info-value">{{ currentVideoInfo.syncTimeStr || '鏈煡' }}</span>
          </div>
          <div class="info-item">
            <span class="info-label">瑙嗛绫诲瀷锛?/span>
            <span class="info-value">{{ currentVideoInfo.viedoCate || '鏈煡' }}</span>
          </div>
          <div class="info-item">
            <a-popover placement="bottom">
              <template #content>
                <p>{{formatPathSeparator(currentVideoInfo?.videoSavePath)}}</p>
              </template>
              <a-button type="link" size="small" @click="copyVideoPath(formatPathSeparator(currentVideoInfo?.videoSavePath))" class="copy-path-btn">
                澶嶅埗璺緞
              </a-button>
            </a-popover>
          </div>
        </div>
      </div>
    </a-modal>

    <!-- 琛ㄦ牸 - 澧炲姞澶嶉€夋鍜屾搷浣滃垪 -->
    <a-table :columns="columns" :data-source="dataSource" bordered :pagination="pagination" @change="handleTableChange" :loading="loading" :row-selection="isBatchMode ? rowSelection : null" row-key="id" :sorter="true">
      <template #bodyCell="{ column, record }">
        <template v-if="column.dataIndex === 'videoTitle'">
          <a class="video-title-link" :title="record.videoTitle || '鏃犳爣棰?" @click="handleVideoClick(record)" @mouseenter="handleTitleMouseEnter" @mouseleave="handleTitleMouseLeave">
            {{ formatVideoTitle(record.videoTitle) }}
          </a>
        </template>
        <template v-if="column.dataIndex === 'subtitleStatus'">
          <a-tag :color="getSubtitleStatusColor(record)" :title="record.subtitleStatusMsg || ''">
            {{ getSubtitleStatusText(record) }}
          </a-tag>
        </template>
        <template v-if="column.key === 'operation'">
          <a-space size="small">
            <a-button type="link" @click="handleReDownload(record)" :disabled="isSyncing">
              <SyncOutlined />
              閲嶆柊鍚屾
            </a-button>
            <a-button type="link" @click="handleShare(record)" :disabled="!record.id">
              <ShareAltOutlined />
              鍒嗕韩
            </a-button>
            <a-button type="link" @click="handleGenerateSubtitle(record)" :disabled="!record.id || isSyncing">
              鐢熸垚瀛楀箷
            </a-button>
            <a-button type="link" @click="handleViewSubtitle(record)" :disabled="!record.subtitleSavePath">
              View subtitle
            </a-button>
            <a-button type="link" danger @click="handleDelete(record)" :disabled="!record.id">
              <DeleteOutlined />
              鍒犻櫎
            </a-button>
          </a-space>
        </template>
      </template>
    </a-table>

    <a-modal
      v-model:visible="isSubtitleModalOpen"
      title="Subtitle Preview"
      :width="900"
      :footer="null"
      @cancel="closeSubtitleModal"
    >
      <div class="subtitle-modal-meta">
        <div><strong>Path:</strong> {{ subtitlePreviewPath || '-' }}</div>
        <div><strong>Created:</strong> {{ subtitlePreviewTime || '-' }}</div>
        <div><strong>Status:</strong> {{ subtitlePreviewStatus || '-' }}</div>
      </div>
      <div v-if="subtitlePreviewLoading" class="subtitle-loading">
        <a-spin tip="Loading subtitle..." />
      </div>
      <pre v-else class="subtitle-preview-content">{{ subtitlePreviewContent || 'No subtitle content.' }}</pre>
    </a-modal>
  </div>
</template>

<script lang="ts" setup>
import { reactive, ref, onMounted, nextTick, watch, computed } from 'vue';
import { useApiStore } from '@/store';
import type { UnwrapRef } from 'vue';
import dayjs, { Dayjs } from 'dayjs';
import locale from 'ant-design-vue/es/date-picker/locale/zh_CN';
import { message, Modal } from 'ant-design-vue';
import CryptoJS from 'crypto-js';
import {
  SearchOutlined,
  SyncOutlined,
  ShareAltOutlined,
  ClearOutlined,
  CopyOutlined,
  DeleteOutlined,
} from '@ant-design/icons-vue';

// 绫诲瀷瀹氫箟
type RangeValue = [Dayjs, Dayjs];
interface DataItem {
  id?: string; // 瑙嗛ID锛堝悗绔繑鍥炵殑瀛楁锛岀敤浜庢嫾鎺ユ挱鏀惧湴鍧€锛?
  videoTitle?: string; // 瑙嗛鏍囬
  syncTimeStr?: string; // 鍚屾鏃堕棿
  viedoTypeStr?: string; // 鍚屾绫诲瀷
  author?: string; // 鍗氫富
  viedoCate?: string; // 瑙嗛绫诲瀷
  dyUser?: string; // CK鍚嶇О
  fileHash?: string;
  authorId?: string;
  videoSavePath: string;
  createTimeStr?: string; // 鍙戝竷鏃堕棿
  isMergeVideo?: boolean;
  subtitleSavePath?: string;
  subtitleCreateTime?: string;
  subtitleStatusMsg?: string;
}

// 馃搶 鏂板锛氭帓搴忓弬鏁扮被鍨嬪畾涔?
interface SortParam {
  field: string; // 鎺掑簭瀛楁
  order: 'ascend' | 'descend' | ''; // 鎺掑簭鏂瑰悜锛氬崌搴?闄嶅簭/鏃?
}
interface QuaryParam {
  dates?: string[];
  dates2?: string[];
  pageIndex: number;
  pageSize: number;
  author: string;
  title: string;
  viedoType: string;
  fileHash: string;
  authorId: string;
  sortField?: string; // 馃搶 鏂板锛氭帓搴忓瓧娈?
  sortOrder?: string; // 馃搶 鏂板锛氭帓搴忔柟鍚戯紙asc/desc锛?
  cookieId?: string;
}

// 寮曞叆dayjs涓枃鍖?
import 'dayjs/locale/zh-cn';
import { forEach } from 'lodash';
dayjs.locale('zh-cn');

// 鎵归噺鎿嶄綔鐩稿叧鐘舵€?
const isBatchMode = ref(false); // 鎵归噺鎿嶄綔寮€鍏崇姸鎬?
const selectedRowKeys = ref<string[]>([]); // 閫変腑鐨勮ID闆嗗悎
// 馃搶 鏂板锛氭帓搴忕姸鎬佺鐞?
const sortParams = ref<SortParam>({
  field: 'syncTime', // 榛樿鎺掑簭瀛楁锛堝彂甯冩椂闂达級
  order: 'descend', // 榛樿闄嶅簭锛堟渶鏂扮殑鍦ㄥ墠锛?
});

// 琛ㄦ牸琛岄€夋嫨鍣ㄧ被鍨嬪畾涔夛紙瀵归綈 Ant Design Vue 3.x 瑙勮寖锛?
interface CustomTableRowSelection<T> {
  type: 'checkbox' | 'radio';
  selectedRowKeys: string[] | number[];
  onChange?: (
    selectedRowKeys: string[] | number[],
    selectedRows: T[],
    info: { type: 'select' | 'unselect' | 'selectAll' | 'unselectAll' | 'clear' }
  ) => void;
  preserveSelectedRowKeys?: boolean;
  getCheckboxProps?: (record: T) => { disabled?: boolean };
}

// 鉁?淇锛氱敤璁＄畻灞炴€у疄鐜板搷搴斿紡缁戝畾锛堣В鍐?checkbox 閫変腑鍗￠】锛?
const rowSelection = computed<CustomTableRowSelection<DataItem>>(() => ({
  type: 'checkbox',
  selectedRowKeys: selectedRowKeys.value, // 璁＄畻灞炴€ц嚜鍔ㄥ悓姝ラ€変腑鐘舵€?
  onChange: (selectedKeys, selectedRows) => {
    selectedRowKeys.value = selectedKeys as string[];
    console.log('閫変腑鐨勮ID锛?, selectedRowKeys.value);
    console.log('閫変腑鐨勮鏁版嵁锛?, selectedRows);
  },
  preserveSelectedRowKeys: false,
  getCheckboxProps: (record) => ({
    disabled: isSyncing.value, // 鍚屾鏃剁鐢ㄥ閫夋锛岄伩鍏嶅啿绐?
  }),
}));

const columns = ref([
  {
    title: '鍚屾鏃堕棿',
    dataIndex: 'syncTimeStr',
    align: 'center',
    width: 180,
    sorter: true, // 寮€鍚帓搴?
    // 缁戝畾鎺掑簭鐘舵€侊細褰撳墠鎺掑簭瀛楁鏄痵yncTime鏃舵樉绀哄搴旀帓搴忔柟鍚?
    sortOrder: sortParams.value.field === 'syncTime' ? sortParams.value.order : null,
    // 鐐瑰嚮琛ㄥご瑙﹀彂鎺掑簭锛屾寚瀹氭帓搴忓瓧娈典负syncTime锛堝搴斿悗绔瓧娈碉級
    onHeaderCell: () => ({
      onClick: () => {
        handleSortChange('syncTime');
      },
    }),
  },
  {
    title: '鍙戝竷鏃堕棿',
    dataIndex: 'createTimeStr',
    align: 'center',
    width: 180,
    sorter: true,
    sortOrder: sortParams.value.field === 'createTime' ? sortParams.value.order : null,
    onHeaderCell: () => ({
      onClick: () => {
        handleSortChange('createTime');
      },
    }),
  },
  {
    title: '鍚屾绫诲瀷',
    dataIndex: 'viedoTypeStr',
    align: 'center',
    width: 120,
  },
  {
    title: '鍗氫富',
    dataIndex: 'author',
    align: 'center',
    width: 150,
    sorter: true,
    sortOrder: sortParams.value.field === 'author' ? sortParams.value.order : null,
    onHeaderCell: () => ({
      onClick: () => {
        handleSortChange('author');
      },
    }),
  },
  {
    title: '瑙嗛绫诲瀷',
    dataIndex: 'viedoCate',
    width: 200,
    align: 'center',
  },
  {
    title: '瑙嗛鏍囬',
    dataIndex: 'videoTitle',
    align: 'left',
    width: 350,
  },
  {
    title: 'Subtitle',
    dataIndex: 'subtitleStatus',
    align: 'center',
    width: 140,
  },
  {
    title: 'CK鍚嶇О',
    dataIndex: 'dyUser',
    align: 'center',
    width: 120,
  },
  {
    title: '鎿嶄綔',
    key: 'operation',
    align: 'center',
    width: 260,
  },
]);

// 馃搶鏀寔鍚屾鏃堕棿/鍙戝竷鏃堕棿/鍗氫富鍒楃殑鎺掑簭鍥炬爣姝ｇ‘鏇存柊
const handleSortChange = (field: string) => {
  // 濡傛灉鐐瑰嚮鐨勬槸褰撳墠鎺掑簭瀛楁锛屽垏鎹㈡帓搴忔柟鍚?
  if (sortParams.value.field === field) {
    sortParams.value.order = sortParams.value.order === 'ascend' ? 'descend' : 'ascend';
  } else {
    // 鏂版帓搴忓瓧娈碉紝榛樿闄嶅簭
    sortParams.value.field = field;
    sortParams.value.order = 'descend';
  }

  // 閬嶅巻鎵€鏈夊垪锛屾牴鎹帓搴忓瓧娈垫槧灏勬洿鏂板搴斿垪鐨剆ortOrder锛堟牳蹇冧慨澶嶏級
  columns.value.forEach((col) => {
    // 瀛楁鏄犲皠锛氬垪鐨刣ataIndex -> 鍚庣鎺掑簭瀛楁sortParams.field
    const fieldMap = {
      syncTimeStr: 'syncTime',
      createTimeStr: 'createTime',
      author: 'author',
    };
    // 鍙湁褰撳墠鎺掑簭瀛楁瀵瑰簲鐨勫垪锛屾樉绀烘帓搴忔柟鍚戯紝鍏朵粬鍒楃疆绌?
    col.sortOrder =
      fieldMap[col.dataIndex as keyof typeof fieldMap] === sortParams.value.field ? sortParams.value.order : null;
  });

  // 閲嶆柊鏌ヨ鏁版嵁锛堜紶閫掓帓搴忓弬鏁帮級
  GetRecords();
};
// 鐩戝惉鎵归噺鎿嶄綔寮€鍏崇姸鎬佸彉鍖栵紝娓呯┖閫変腑鐘舵€?寮哄埗琛ㄦ牸閲嶇粯
watch(isBatchMode, (isOpen) => {
  if (!isOpen) {
    selectedRowKeys.value = [];
    // 寮哄埗琛ㄦ牸閲嶆柊娓叉煋锛岃В鍐崇姸鎬佹畫鐣欓棶棰?
    nextTick(() => {
      const tableEl = document.querySelector('.ant-table') as HTMLElement;
      if (tableEl) {
        tableEl.setAttribute('key', Date.now().toString());
      }
    });
  }
});

// 鍩虹鐘舵€侊紙浼樺寲锛氬垹闄ゅ啑浣欑殑 datas 鍝嶅簲寮忔暟缁勶級
const loading = ref(false);
const showImageViedo = ref(true);
const dataSource = ref<DataItem[]>([]); // 鐩存帴鐢?ref 鏁扮粍瀛樺偍琛ㄦ牸鏁版嵁锛屽噺灏戝搷搴斿紡宓屽

// 鏌ヨ鍙傛暟
const value1 = ref<RangeValue>();
const ranges = {
  浠婂ぉ: [dayjs(), dayjs()] as RangeValue,
  鏈湀: [dayjs(), dayjs().endOf('month')] as RangeValue,
};

const value2 = ref<RangeValue>();
const ranges2 = {
  浠婂ぉ: [dayjs(), dayjs()] as RangeValue,
  鏈湀: [dayjs(), dayjs().endOf('month')] as RangeValue,
};
const quaryData: UnwrapRef<QuaryParam> = reactive({
  pageIndex: 0,
  pageSize: 20,
  author: '',
  title: '',
  viedoType: '*',
  authorId: '',
  fileHash: '',
  sortField: 'createTime', // 馃搶 榛樿鎺掑簭瀛楁
  sortOrder: 'desc', // 馃搶 榛樿闄嶅簭
  cookieId: '',
});

// 鍒嗛〉閰嶇疆
const pagination = ref({
  current: 1,
  defaultPageSize: 10,
  total: 0,
  showSizeChanger: true, // 寮哄埗鏄剧ず銆屾瘡椤垫樉绀烘暟閲忋€嶄笅鎷夋锛堝叧閿慨澶嶏級
  showTotal: () => `鍏?${0} 鏉,
  // showQuickJumper: true, // 鏄剧ず蹇€熻烦杞緭鍏ユ锛堝彲閫夛紝澧炲己浣撻獙锛?
  pageSizeOptions: ['10', '20', '50', '100'], // 鑷畾涔夋瘡椤垫潯鏁伴€夐」锛堝彲閫夛級
  showSizeChange: (current, pageSize) => {
    // 鍙€夛細鐩戝惉姣忛〉鏉℃暟鍙樺寲锛岄噸缃綋鍓嶉〉涓虹1椤碉紙閬垮厤鏈€鍚庝竴椤垫暟鎹笉瓒崇殑闂锛?
    pagination.value.current = 1;
    pagination.value.defaultPageSize = pageSize;
    GetRecords();
  },
});

// 瑙嗛鎾斁鐩稿叧閰嶇疆
const DEFAULT_LOW_VOLUME = 0.3;
const isVideoLoading = ref(false); // 瑙嗛鍔犺浇鐘舵€?
const isSyncing = ref(false); // 鍚屾鐘舵€?
const currentVideoInfo = ref<DataItem | null>(null); // 褰撳墠鎾斁瑙嗛淇℃伅

// 瑙嗛寮圭獥鐩稿叧鐘舵€?
const isModalOpen = ref(false);
const videoRef = ref<HTMLVideoElement | null>(null);
const videoUrl = ref('');
const hasError = ref(false);
const errorMessage = ref('');
const autoPlay = ref(true);
const autoMuted = ref(true);
const videoId = ref('');
const playingTitle = ref('');
let videoProgressListener: ((e: Event) => void) | null = null; // 杩涘害鐩戝惉鍣?
const isSubtitleModalOpen = ref(false);
const subtitlePreviewLoading = ref(false);
const subtitlePreviewContent = ref('');
const subtitlePreviewPath = ref('');
const subtitlePreviewTime = ref('');
const subtitlePreviewStatus = ref('');

/** 鏍煎紡鍖栧瓨鍌ㄨ矾寰勶紙杩囬暱鏃朵腑闂寸渷鐣ワ級 */
const formatFilePath = (filePath?: string) => {
  if (!filePath) return '鏆傛棤瀛樺偍璺緞淇℃伅';
  // 璺緞瓒呰繃80瀛楃鏃讹紝淇濈暀鍓?0鍜屽悗30瀛楃锛屼腑闂寸敤...鐪佺暐
  if (filePath.length > 80) {
    return `${filePath.slice(0, 40)}...${filePath.slice(-30)}`;
  }
  return filePath;
};

// -------------------------- 鏍稿績宸ュ叿鏂规硶 --------------------------

const formatPathSeparator = (path: string | undefined) => {
  if (!path) return path; // 澶勭悊绌鸿矾寰勬儏鍐?
  // 姝ｅ垯琛ㄨ揪寮?/\\/g 琛ㄧず鍏ㄥ眬鍖归厤鎵€鏈夊弽鏂滄潬
  return path.replace(/\\/g, '/');
};
/** 鏍煎紡鍖栬〃鏍艰棰戞爣棰橈細瓒呰繃20瀛楃鏄剧ず鐪佺暐鍙?*/
const formatVideoTitle = (title?: string) => {
  if (!title) return '鏃犳爣棰?;
  return title.length > 20 ? `${title.slice(0, 20)}...` : title;
};

/** 鏍煎紡鍖栧脊绐楁爣棰橈細瓒呰繃40瀛楃鏄剧ず鐪佺暐鍙?*/
const formatModalTitle = (title?: string) => {
  if (!title) return '瑙嗛鎾斁';
  return title.length > 40 ? `${title.slice(0, 40)}...` : title;
};

const getSubtitleStatusText = (record: DataItem) => {
  if (record.subtitleSavePath) {
    return 'Ready';
  }

  if (record.subtitleStatusMsg) {
    return 'Failed';
  }

  return 'None';
};

const getSubtitleStatusColor = (record: DataItem) => {
  if (record.subtitleSavePath) {
    return 'green';
  }

  if (record.subtitleStatusMsg) {
    return 'red';
  }

  return 'default';
};

/** 鏍囬榧犳爣杩涘叆浜嬩欢锛氭坊鍔犱笅鍒掔嚎 */
const handleTitleMouseEnter = (e: Event) => {
  const target = e.target as HTMLElement;
  target.style.textDecoration = 'underline';
};

/** 鏍囬榧犳爣绂诲紑浜嬩欢锛氱Щ闄や笅鍒掔嚎 */
const handleTitleMouseLeave = (e: Event) => {
  const target = e.target as HTMLElement;
  target.style.textDecoration = 'none';
};

// -------------------------- 鏍稿績涓氬姟鏂规硶 --------------------------
/** 鏌ヨ琛ㄦ牸鏁版嵁 */
const GetRecords = () => {
  loading.value = true;
  quaryData.pageIndex = pagination.value.current;
  quaryData.pageSize = pagination.value.defaultPageSize;

  if (value1.value) {
    quaryData.dates = value1.value.map((date) => date.format('YYYY-MM-DD'));
  }
  if (value2.value) {
    quaryData.dates2 = value2.value.map((date) => date.format('YYYY-MM-DD')); // 淇锛氫箣鍓嶈鍐欎负value1
  }
  // 馃搶 鍏抽敭锛氬皢鍓嶇鎺掑簭鐘舵€佽浆鎹负鍚庣闇€瑕佺殑鍙傛暟
  quaryData.sortField = sortParams.value.field;
  // 杞崲鎺掑簭鏂瑰悜锛坅ntd鐨刟scend/descend 杞?鍚庣甯哥敤鐨刟sc/desc锛?
  quaryData.sortOrder = sortParams.value.order === 'ascend' ? 'asc' : 'desc';
  useApiStore()
    .VideoPageList(quaryData)
    .then((res) => {
      loading.value = false;
      if (res.code === 0) {
        dataSource.value = res.data.data; // 鐩存帴鏇存柊 ref 鏁扮粍锛屼紭鍖栧搷搴斿紡
        pagination.value.current = res.data.pageIndex;
        pagination.value.defaultPageSize = res.data.pageSize;
        pagination.value.total = res.data.total;
        pagination.value.showTotal = () => `鍏?${res.data.total} 鏉;
      } else {
        message.warning(res.message || '鑾峰彇鏁版嵁澶辫触');
      }
    })
    .catch((error) => {
      loading.value = false;
      console.error('鑾峰彇琛ㄦ牸鏁版嵁澶辫触:', error);
      message.error('鑾峰彇鏁版嵁澶辫触锛岃绋嶅悗閲嶈瘯');
    });
};

// 馃搶 淇锛氬垎椤垫椂鏃犳帓搴忔搷浣滐紝寮哄埗淇濈暀榛樿syncTime鎺掑簭
const handleTableChange = (paginationObj: any, filters: any, sorter: any) => {
  pagination.value.current = paginationObj.current;
  pagination.value.defaultPageSize = paginationObj.pageSize;

  // 1. 濡傛灉鏄帓搴忓彉鍖栵紙鐢ㄦ埛鐐瑰嚮琛ㄥご锛夛紝鏇存柊鎺掑簭鍙傛暟
  if (sorter.field) {
    // 鍒梔ataIndex -> 鍚庣鎺掑簭瀛楁鐨勬槧灏?
    const fieldMap: Record<string, string> = {
      syncTimeStr: 'syncTime',
      createTimeStr: 'createTime',
      author: 'author',
    };
    // 杞崲鎺掑簭瀛楁
    sortParams.value.field = fieldMap[sorter.field] || sorter.field;
    sortParams.value.order = sorter.order;

    // 鏇存柊鎵€鏈夊垪鐨勬帓搴忓浘鏍?
    columns.value.forEach((col) => {
      col.sortOrder = fieldMap[col.dataIndex as string] === sortParams.value.field ? sorter.order : null;
    });
  }
  // 2. 鍒嗛〉璺宠浆锛堟棤鎺掑簭鎿嶄綔锛夛紝寮哄埗鎭㈠榛樿鎺掑簭syncTime鐨勫浘鏍囩姸鎬?
  else if (!sorter.field && sortParams.value.field !== 'syncTime') {
    // 閲嶇疆鎺掑簭鍙傛暟涓洪粯璁わ細syncTime 闄嶅簭
    sortParams.value.field = 'syncTime';
    sortParams.value.order = 'descend';
    // 鍒锋柊鍒楃殑鎺掑簭鍥炬爣锛屽彧鏄剧ず鍚屾鏃堕棿鍒楃殑闄嶅簭
    columns.value.forEach((col) => {
      col.sortOrder = col.dataIndex === 'syncTimeStr' ? 'descend' : null;
    });
  }

  // 鍒嗛〉鍙樺寲鏃舵竻绌洪€変腑鐘舵€?
  if (isBatchMode.value) {
    selectedRowKeys.value = [];
  }

  // 閲嶆柊鏌ヨ鏁版嵁锛堟惡甯︽纭殑鎺掑簭鍙傛暟锛?
  GetRecords();
};

const cookies = ref([]);
const getCookies = () => {
  useApiStore()
    .CookiePageList({})
    .then((res) => {
      if (res.data.data.length > 0) {
        cookies.value = res.data.data.map((item) => {
          return {
            value: item['id'] ?? '',
            label: item['userName'] ?? '',
          };
        });
        cookies.value.unshift({
          value: '', // 鍏ㄩ儴瀵瑰簲鐨?value 涓虹┖瀛楃涓?
          label: '鍏ㄩ儴', // 鏄剧ず鐨勬枃鏈紝鍙牴鎹渶姹備慨鏀?
        });

        quaryData.cookieId = cookies.value[0].value;
        GetRecords();
      }
    });
};

/** 绔嬪嵆鍚屾 */
const StartNow = () => {
  if (isSyncing.value) return;
  message.success('璇疯€愬績绛夊緟锛屽悓姝ヤ换鍔℃鍦ㄥ惎鍔?..');
  isSyncing.value = true;
  useApiStore()
    .StartJobNow()
    .then((res) => {
      if (res.code === 0) {
        message.success('鍚屾浠诲姟鍚姩鎴愬姛锛?);
        GetRecords();
      } else {
        message.error(`鍚屾浠诲姟鍚姩澶辫触: ${res.message || '鏈煡閿欒'}`);
      }
    })
    .catch((error) => {
      console.error('鍚屾浠诲姟API璋冪敤澶辫触:', error);
      message.error('鍚屾浠诲姟鍚姩澶辫触锛岃妫€鏌ョ綉缁滄垨鑱旂郴绠＄悊鍛?);
    })
    .finally(() => {
      isSyncing.value = false;
    });
};

/** 鍚屾鏃ユ湡閫夋嫨鍣ㄥ彉鍖栦簨浠?*/
const datePicked = (_, dateArry: RangeValue) => {
  quaryData.dates = dateArry.map((date) => date.format('YYYY-MM-DD'));
  console.log('閫夋嫨鐨勫悓姝ユ棩鏈熻寖鍥?', quaryData.dates);
};

/** 鍙戝竷鏃ユ湡閫夋嫨鍣ㄥ彉鍖栦簨浠?*/
const datePicked2 = (_, dateArry: RangeValue) => {
  quaryData.dates2 = dateArry.map((date) => date.format('YYYY-MM-DD'));
  console.log('閫夋嫨鐨勫彂甯冩棩鏈熻寖鍥?', quaryData.dates2);
};

/** 琛ㄦ牸鍒嗛〉/鎺掑簭鍙樺寲浜嬩欢 */
// const handleTableChange = (paginationObj: any) => {
//   pagination.value.current = paginationObj.current;
//   pagination.value.defaultPageSize = paginationObj.pageSize;
//   // 鍒嗛〉鍙樺寲鏃舵竻绌洪€変腑鐘舵€侊紙璺ㄩ〉涓嶄繚鐣欙級
//   if (isBatchMode.value) {
//     selectedRowKeys.value = [];
//   }
//   GetRecords();
// };

/** 瑙嗛绫诲瀷鍒囨崲浜嬩欢 */
const onViedoTypeChanged = () => {
  GetRecords();
};

// -------------------------- 瑙嗛鎾斁鐩稿叧鏂规硶 --------------------------
/** 鐐瑰嚮瑙嗛鏍囬鎾斁 */
const handleVideoClick = (record: DataItem) => {
  if (record.isMergeVideo && record.videoSavePath.length == 0) {
    message.warning('鍥炬枃瑙嗛閰嶇疆锛氫笉涓嬭浇瑙嗛锛屾墍鏈夋病鏈夊彲鎾斁鐨勮棰?);
    return;
  }
  // 淇濆瓨褰撳墠瑙嗛淇℃伅
  currentVideoInfo.value = record;
  console.log(currentVideoInfo);
  videoId.value = record.id;
  playingTitle.value = formatModalTitle(record.videoTitle);
  // 閲嶇疆閿欒鐘舵€?
  hasError.value = false;
  // 鏄剧ず寮圭獥锛堣Е鍙憌atch鍔犺浇瑙嗛锛?
  isModalOpen.value = true;
};

/** 鍔犺浇瑙嗛锛堜紭鍖栵細绠€鍖栭€昏緫锛岄伩鍏嶅唴瀛樻硠婕忥級 */
const loadVideo = () => {
  if (!videoRef.value || !videoId.value) return;

  isVideoLoading.value = true;

  // 绉婚櫎涔嬪墠鐨勭洃鍚櫒
  if (videoProgressListener) {
    videoRef.value.removeEventListener('progress', videoProgressListener);
    videoProgressListener = null;
  }

  // 鎷兼帴瑙嗛鍦板潃锛堟坊鍔犳椂闂存埑閬垮厤缂撳瓨锛?
  const timestamp = new Date().getTime();
  videoUrl.value = `${import.meta.env.VITE_API_URL}api/Video/play/${videoId.value}?t=${timestamp}`;

  // 鐩存帴璧嬪€約rc骞跺姞杞?
  videoRef.value.src = videoUrl.value;

  // 閲嶆柊缁戝畾杩涘害鐩戝惉鍣?
  videoProgressListener = handleVideoProgress;
  videoRef.value.addEventListener('progress', videoProgressListener);

  // 瑙﹀彂鍔犺浇
  videoRef.value.load();
};

/** 瑙嗛鍔犺浇杩涘害澶勭悊 */
const handleVideoProgress = (e: Event) => {
  const video = e.target as HTMLVideoElement;
  if (video.buffered.length > 0) {
    const bufferedEnd = video.buffered.end(video.buffered.length - 1);
    const duration = video.duration;
    // 缂撳啿杈惧埌90%浠ヤ笂闅愯棌鍔犺浇鍔ㄧ敾
    if (duration > 0 && bufferedEnd / duration > 0.9) {
      isVideoLoading.value = false;
    }
  }
};

/** 鏆傚仠瑙嗛骞堕噴鏀捐祫婧?*/
const pauseVideo = () => {
  if (!videoRef.value) return;

  const video = videoRef.value;
  // 鏆傚仠鎾斁
  video.pause();
  // 绉婚櫎鐩戝惉鍣?
  if (videoProgressListener) {
    video.removeEventListener('progress', videoProgressListener);
    videoProgressListener = null;
  }
  // 娓呯┖src
  video.src = '';
  // 閲嶇疆鐘舵€?
  isVideoLoading.value = false;
};

/** 瑙嗛閿欒澶勭悊 */
const handleVideoError = (e: Event) => {
  const video = e.target as HTMLVideoElement;
  const errorCode = video.error?.code;

  const errorMap: Record<number, string> = {
    1: '瑙嗛鍔犺浇涓柇',
    2: '缃戠粶閿欒锛堣法鍩熸湭閰嶇疆/鍚庣鏈嶅姟鏈惎鍔?鎺ュ彛涓嶅彲鐢級',
    3: '瑙嗛瑙ｇ爜澶辫触锛堟牸寮忎笉鏀寔鎴栨枃浠舵崯鍧忥級',
    4: '瑙嗛鏍煎紡涓嶆敮鎸?,
    5: '瑙嗛鏂囦欢涓嶅瓨鍦ㄦ垨鍚庣鏉冮檺涓嶈冻',
  };

  if (!video.src) {
    errorMessage.value = '瑙嗛鍦板潃涓虹┖锛岃閲嶈瘯';
  } else {
    errorMessage.value = `鍔犺浇澶辫触锛?{errorMap[errorCode as number] || '鏈煡閿欒'}锛堣棰慖D锛?{videoId.value}锛塦;
  }

  hasError.value = true;
  isVideoLoading.value = false;
  console.error('瑙嗛鎾斁閿欒璇︽儏锛?, video.error);
};

/** 鍏抽棴瑙嗛寮圭獥 */
const handleCancel = () => {
  // 鏆傚仠瑙嗛骞堕噴鏀捐祫婧?
  pauseVideo();
  // 绔嬪嵆鍏抽棴寮圭獥
  isModalOpen.value = false;
  // 寤惰繜閲嶇疆鐘舵€?
  setTimeout(() => {
    currentVideoInfo.value = null;
    videoUrl.value = '';
    videoId.value = '';
    playingTitle.value = '';
  }, 100);
};

// 鐩戝惉寮圭獥鐘舵€侊紝鍔犺浇/閲婃斁瑙嗛
watch(
  isModalOpen,
  (isOpen) => {
    if (isOpen) {
      // 寮圭獥鎵撳紑鏃讹紝寤惰繜鍔犺浇瑙嗛锛堢粰DOM娓叉煋鏃堕棿锛?
      nextTick(() => {
        loadVideo();
      });
    } else {
      // 寮圭獥鍏抽棴鏃讹紝绔嬪嵆鏆傚仠瑙嗛
      pauseVideo();
    }
  },
  { immediate: false }
);

// -------------------------- 鎵归噺鎿嶄綔鍜屾搷浣滃垪浜嬩欢 --------------------------
/** 鎵归噺鍒犻櫎浜嬩欢 */
const handleBatchSync = () => {
  if (selectedRowKeys.value.length === 0) {
    message.warning('璇峰厛閫夋嫨瑕侀噸鏂颁笅杞界殑瑙嗛');
    return;
  }

  Modal.confirm({
    title: '纭閲嶆柊涓嬭浇鍚?,
    content: `鎮ㄧ‘瀹氳閲嶆柊涓嬭浇閫変腑鐨?${selectedRowKeys.value.length} 鏉¤棰戞暟鎹悧锛焋,
    okText: '纭閲嶆柊涓嬭浇',
    cancelText: '鍙栨秷',
    okType: 'danger',
    onOk: async () => {
      reDownload({ ids: selectedRowKeys.value });
    },
  });
};

const handleBatchDelete = () => {
  if (selectedRowKeys.value.length === 0) {
    message.warning('璇峰厛閫夋嫨瑕佸交搴曞垹闄ょ殑瑙嗛');
    return;
  }

  Modal.confirm({
    title: '纭鍒犻櫎杩欎簺涓嬭浇鐨勮棰戝悧',
    content: `鎮ㄧ‘瀹氳褰诲簳涓嬪垹闄ら€変腑鐨?${selectedRowKeys.value.length} 鏉¤棰戞暟鎹悧锛焋,
    okText: '纭褰诲簳鍒犻櫎',
    cancelText: '鍙栨秷',
    okType: 'danger',
    onOk: async () => {
      deleteBatch({ ids: selectedRowKeys.value });
    },
  });
};

const handleBatchAsr = () => {
  if (selectedRowKeys.value.length === 0) {
    message.warning('Please select videos first');
    return;
  }

  Modal.confirm({
    title: 'Generate subtitles?',
    content: `Generate local subtitles for ${selectedRowKeys.value.length} selected videos?`,
    okText: 'Generate',
    cancelText: 'Cancel',
    onOk: async () => {
      generateSubtitleBatch({ ids: selectedRowKeys.value });
    },
  });
};

const deleteVideoShow = ref(false);
const handShowDeleteVideos = () => {
  deleteVideoShow.value = true;
  getDeleteViedos();
};

const deleteVideos = ref([]);
const getDeleteViedos = () => {
  useApiStore()
    .GetDeleteViedos()
    .then((res) => {
      deleteVideos.value = res.data;
    });
};
const onDeleteVideoClose = (e) => {
  deleteVideoShow.value = false;
};

const reDownload = (param: object) => {
  try {
    loading.value = true;
    console.log('鎵ц鎵归噺鍒犻櫎锛岄€変腑ID锛?, selectedRowKeys.value);

    useApiStore()
      .ReDownViedos(param)
      .then((res) => {
        loading.value = false;
        if (res.code === 0) {
          message.success('鍒犻櫎鎴愬姛锛屼笅娆′换鍔℃墽琛屾椂浼氶噸鏂颁笅杞?);
          // 鍒锋柊鏁版嵁骞舵竻绌洪€変腑鐘舵€?
          GetRecords();
          selectedRowKeys.value = [];
        } else {
          message.warning(res.message || '鑾峰彇鏁版嵁澶辫触');
        }
      })
      .catch((error) => {
        loading.value = false;
      });
  } catch (error) {
    console.error('鎵归噺鍒犻櫎澶辫触锛?, error);
    message.error('鍒犻櫎澶辫触锛岃绋嶅悗閲嶈瘯');
  } finally {
    loading.value = false;
  }
};

const generateSubtitleBatch = (param: object) => {
  try {
    loading.value = true;

    useApiStore()
      .GenerateSubtitleBatch(param)
      .then((res) => {
        loading.value = false;
        if (res.code === 0) {
          const successCount = res.data?.successCount ?? 0;
          const failedCount = res.data?.failedCount ?? 0;
          message.success(`Subtitle generation finished. Success: ${successCount}, Failed: ${failedCount}`);
          GetRecords();
          selectedRowKeys.value = [];
        } else {
          message.warning(res.message || 'Batch subtitle generation failed');
        }
      })
      .catch(() => {
        loading.value = false;
      });
  } catch (error) {
    console.error('Batch subtitle generation failed:', error);
    message.error('Batch subtitle generation failed');
  } finally {
    loading.value = false;
  }
};

const deleteBatch = (param: object) => {
  try {
    loading.value = true;
    console.log('鎵ц鎵归噺鍒犻櫎锛岄€変腑ID锛?, selectedRowKeys.value);

    useApiStore()
      .BathRealDelete(param)
      .then((res) => {
        loading.value = false;
        if (res.code === 0) {
          message.success('鍒犻櫎鎴愬姛锛屼互鍚庨兘涓嶄細涓嬭浇浜嗗摝锛屼綘鑷繁閫夌殑');
          // 鍒锋柊鏁版嵁骞舵竻绌洪€変腑鐘舵€?
          GetRecords();
          selectedRowKeys.value = [];
        } else {
          message.warning(res.message || '鑾峰彇鏁版嵁澶辫触');
        }
      })
      .catch((error) => {
        loading.value = false;
      });
  } catch (error) {
    console.error('鎵归噺鍒犻櫎澶辫触锛?, error);
    message.error('鍒犻櫎澶辫触锛岃绋嶅悗閲嶈瘯');
  } finally {
    loading.value = false;
  }
};

/** 閲嶆柊涓嬭浇浜嬩欢 */
const handleReDownload = (record: DataItem) => {
  if (!record.id) {
    message.warning('瑙嗛ID涓嶅瓨鍦紝鏃犳硶閲嶆柊涓嬭浇');
    return;
  }

  try {
    loading.value = true;
    const _ids = [record.id];
    reDownload({ ids: _ids });
  } catch (error) {
    console.error('閲嶆柊涓嬭浇澶辫触锛?, error);
    message.error('閲嶆柊涓嬭浇澶辫触锛岃绋嶅悗閲嶈瘯');
    loading.value = false;
  }
};

const handleGenerateSubtitle = (record: DataItem) => {
  if (!record.id) {
    message.warning('Video id is missing');
    return;
  }

  Modal.confirm({
    title: 'Generate subtitle?',
    content: `Generate local subtitle for "${record.videoTitle || 'Untitled'}"?`,
    okText: 'Generate',
    cancelText: 'Cancel',
    onOk: async () => {
      generateSubtitle(record.id);
    },
  });
};

const generateSubtitle = (id: string) => {
  if (isSyncing.value) return;
  isSyncing.value = true;
  useApiStore()
    .GenerateSubtitle(id)
    .then((res) => {
      if (res.code === 0) {
        message.success(res.data?.message || 'Subtitle generated');
        GetRecords();
      } else {
        message.error(res.message || 'Subtitle generation failed');
      }
    })
    .catch((error) => {
      console.error('Subtitle generation failed:', error);
      message.error('Subtitle generation failed');
    })
    .finally(() => {
      isSyncing.value = false;
    });
};

const closeSubtitleModal = () => {
  isSubtitleModalOpen.value = false;
};

const handleViewSubtitle = (record: DataItem) => {
  if (!record.id) {
    message.warning('Video id is missing');
    return;
  }

  subtitlePreviewContent.value = '';
  subtitlePreviewPath.value = '';
  subtitlePreviewTime.value = '';
  subtitlePreviewStatus.value = record.subtitleStatusMsg || '';
  subtitlePreviewLoading.value = true;
  isSubtitleModalOpen.value = true;

  useApiStore()
    .GetSubtitleContent(record.id)
    .then((res) => {
      if (res.code === 0) {
        subtitlePreviewContent.value = res.data?.content || '';
        subtitlePreviewPath.value = res.data?.subtitlePath || record.subtitleSavePath || '';
        subtitlePreviewTime.value = res.data?.subtitleCreateTime || '';
        subtitlePreviewStatus.value = res.data?.statusMessage || getSubtitleStatusText(record);
      } else {
        subtitlePreviewContent.value = '';
        subtitlePreviewStatus.value = res.message || 'Failed to load subtitle';
        message.warning(res.message || 'Failed to load subtitle');
      }
    })
    .catch((error) => {
      console.error('Load subtitle failed:', error);
      subtitlePreviewContent.value = '';
      subtitlePreviewStatus.value = 'Failed to load subtitle';
      message.error('Failed to load subtitle');
    })
    .finally(() => {
      subtitlePreviewLoading.value = false;
    });
};

const handleBatchShare = () => {
  const matchedItems = dataSource.value.filter((item) => selectedRowKeys.value.includes(item.id));
  try {
    // console.log('鎵ц鍒嗕韩鎿嶄綔锛岃棰慖D锛?, record.id, '瑙嗛鏍囬锛?, record.videoTitle);
    // 鐢熸垚鍒嗕韩閾炬帴
    const currentDomain = window.location.origin;
    let shareUrl = '';
    matchedItems.forEach((record) => {
      let k = CryptoJS.MD5(record.fileHash + record.authorId).toString();
      shareUrl += `${currentDomain}/share/${record.id}/${k}
      `;
    });
    copyToClipboard(shareUrl, '鍒嗕韩閾炬帴宸插鍒跺埌鍓创鏉匡紒');
  } catch (error) {
    console.error('鍒嗕韩澶辫触锛?, error);
    message.error('鍒嗕韩鍔熻兘寮傚父锛岃绋嶅悗閲嶈瘯');
  }
};

// 澶嶅埗閾炬帴鍒板壀璐存澘锛堝吋瀹圭敓浜х幆澧冿級
const copyToClipboard = async (shareUrl: string, msg: string) => {
  try {
    // 鏂规1锛氫紭鍏堜娇鐢?navigator.clipboard锛堢幇浠ｆ祻瑙堝櫒+HTTPS鐜锛?
    if (navigator.clipboard && typeof navigator.clipboard.writeText === 'function') {
      await navigator.clipboard.writeText(shareUrl);
      message.success(msg);
    } else {
      // 鏂规2锛氶檷绾т娇鐢?document.execCommand锛堝吋瀹笻TTP/鏃ф祻瑙堝櫒锛?
      const textarea = document.createElement('textarea');
      // 闅愯棌鏂囨湰鍩燂紙閬垮厤褰卞搷椤甸潰甯冨眬锛?
      textarea.style.position = 'absolute';
      textarea.style.top = '-9999px';
      textarea.style.left = '-9999px';
      // 璁剧疆瑕佸鍒剁殑鍐呭
      textarea.value = shareUrl;
      document.body.appendChild(textarea);
      // 閫変腑骞跺鍒?
      textarea.select();
      const success = document.execCommand('copy');
      document.body.removeChild(textarea); // 娓呯悊DOM

      if (success) {
        message.success(msg);
      } else {
        // 鏂规3锛氭渶缁堥檷绾?- 鏄剧ず閾炬帴璁╃敤鎴锋墜鍔ㄥ鍒?
        throw new Error('鑷姩澶嶅埗澶辫触');
      }
    }
  } catch (error) {
    console.warn('澶嶅埗澶辫触锛岃Е鍙戞墜鍔ㄥ鍒舵柟妗堬細', error);
    // 鏈€缁堥檷绾э細鏄剧ず閾炬帴寮圭獥
    Modal.info({
      title: '瑙嗛鍒嗕韩',
      content: `
        <p>鍒嗕韩閾炬帴锛?a href="${shareUrl}" target="_blank" rel="noopener noreferrer">${shareUrl}</a></p>
        <p style="margin-top: 8px; color: #666;">璇锋墜鍔ㄥ鍒堕摼鎺ュ悗鍒嗕韩缁欎粬浜?/p>
      `,
      okText: '宸插鍒?,
      onOk: () => {},
    });
  }
};
/** 鍒嗕韩浜嬩欢 */
const handleShare = (record: DataItem) => {
  if (!record.id) {
    message.warning('瑙嗛ID涓嶅瓨鍦紝鏃犳硶鍒嗕韩');
    return;
  }

  try {
    const currentDomain = window.location.origin;
    // console.log('鎵ц鍒嗕韩鎿嶄綔锛岃棰慖D锛?, record.id, '瑙嗛鏍囬锛?, record.videoTitle);
    // 鐢熸垚鍒嗕韩閾炬帴
    let k = CryptoJS.MD5(record.fileHash + record.authorId).toString();
    const shareUrl = `${currentDomain}/share/${record.id}/${k}`;
    copyToClipboard(shareUrl, '鍒嗕韩閾炬帴宸插鍒跺埌鍓创鏉匡紒');
  } catch (error) {
    console.error('鍒嗕韩澶辫触锛?, error);
    message.error('鍒嗕韩鍔熻兘寮傚父锛岃绋嶅悗閲嶈瘯');
  }
};

//瑙嗛鍒犻櫎涓嶅啀涓嬭浇
const handleDelete = (record: DataItem) => {
  Modal.confirm({
    title: '纭鍒犻櫎',
    content: `鎮ㄧ‘瀹氳鍒犻櫎杩欐潯瑙嗛鏁版嵁鍚楋紵姝ゆ搷浣滀笉鍙挙閿€锛屼互鍚庝篃涓嶄細鍐嶄笅杞斤紒锛侊紒`,
    okText: '纭鍒犻櫎',
    cancelText: '鍙栨秷',
    okType: 'danger',
    onOk: async () => {
      try {
        useApiStore()
          .DeleteVideo(record.id)
          .then((res) => {
            if (res.code == 0) {
              message.success('鍒犻櫎鎴愬姛,鍐嶄篃涓嶄細涓嬭浇锛侊紒锛?);
            } else {
              message.error('鍒犻櫎澶辫触');
            }
            GetRecords();
          });
      } catch (error) {
        console.error('鍒犻櫎澶辫触', error);
        message.error('瑙嗛鍒犻櫎澶辫触锛岃绋嶅悗鍐嶈瘯');
      }
    },
  });
};

// 鏂板锛氬鍒惰棰戣矾寰勬柟娉?
const copyVideoPath = (path?: string) => {
  if (!path) {
    message.warning('鏆傛棤瑙嗛瀛樺偍璺緞');
    return;
  }
  copyToClipboard(path, '瑙嗛淇濆瓨璺緞宸插鍒跺埌鍓创鏉匡紒');
};

// -------------------------- 椤甸潰鍒濆鍖?--------------------------
onMounted(() => {
  // getConfig();
  getCookies();
});
</script>

<style>
.subtitle-modal-meta {
  margin-bottom: 16px;
  color: #555;
  word-break: break-all;
}

.subtitle-loading {
  padding: 32px 0;
  text-align: center;
}

.subtitle-preview-content {
  max-height: 60vh;
  overflow: auto;
  margin: 0;
  padding: 16px;
  white-space: pre-wrap;
  word-break: break-word;
  background: #fafafa;
  border: 1px solid #f0f0f0;
  border-radius: 6px;
}

/* 鏂板锛氫紭鍖栬棰戝厓绱犵殑杩囨浮鏁堟灉锛岄伩鍏嶅叧闂椂鐨勮瑙夊崱椤?*/
.video-element {
  width: 100%;
  height: auto;
  max-height: 420px;
  min-height: 250px;
  background-color: #000;
  object-fit: contain;
  opacity: 1;
  transition: opacity 0.2s ease-in-out; /* 缂╃煭杩囨浮鏃堕棿 */
  will-change: opacity; /* 鍛婅瘔娴忚鍣ㄦ彁鍓嶄紭鍖栨覆鏌?*/
}
/* 鏂板锛氭煡璇㈠尯鍩熸牱寮忎紭鍖?*/
.query-container {
  margin: 16px 0;
  padding: 16px;
  border-radius: 8px;
  box-shadow: 0 1px 2px rgba(0, 0, 0, 0.05);
}

.query-form {
  width: 100%;
}

.form-row {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  margin-bottom: 12px;
}

.form-row:last-child {
  margin-bottom: 0;
}

.form-item {
  margin-bottom: 0 !important;
  margin-right: 24px !important;
  display: flex;
  align-items: center;
}

/* 鏍稿績淇敼锛氫富鏌ヨ琛岃嚜閫傚簲甯冨眬 */
.form-main-row {
  display: flex;
  flex-wrap: nowrap; /* 绂佹鎹㈣ */
  align-items: center;
  width: 100%;
  overflow: hidden; /* 闃叉婧㈠嚭 */
}

/* 鏃ユ湡閫夋嫨鍣ㄩ」锛氬浐瀹氬熀纭€瀹藉害锛岃嚜閫傚簲鏀剁缉 */
.form-item-date {
  flex: 0 1 280px; /* 涓嶆斁澶э紝鍙缉灏忥紝鍩虹瀹藉害280px */
  min-width: 220px; /* 鏈€灏忓搴︼紝闃叉杩囧害鏀剁缉 */
}

/* 杈撳叆妗嗛」锛氳嚜閫傚簲鎷変几濉厖鍓╀綑绌洪棿 */
.form-item-input {
  flex: 1 1 auto; /* 鍙斁澶э紝鍙缉灏忥紝鑷姩瀹藉害 */
  min-width: 180px; /* 鏈€灏忓搴︼紝淇濊瘉鍙敤鎬?*/
}

/* 鏃ユ湡閫夋嫨鍣ㄨ嚜閫傚簲瀹藉害 */
.range-picker {
  width: 100% !important; /* 鍗犳弧鐖跺鍣ㄥ搴?*/
  min-width: 200px !important;
}

/* 杈撳叆妗嗚嚜閫傚簲瀹藉害 */
.query-input {
  width: 100% !important; /* 鍗犳弧鐖跺鍣ㄥ搴?*/
  min-width: 160px !important;
}

/* 鏂板锛氭壒閲忔搷浣滃紑鍏虫牱寮?*/
.batch-operation-item {
  margin-left: 20px !important;
}

.batch-switch {
  --ant-switch-height: 24px;
  --ant-switch-width: 80px;
}

/* 鏂板锛氬垹闄ゆ寜閽牱寮?*/
.delete-button {
  min-width: 100px;
}

/* 鍗曢€夌粍鏍峰紡 */
.video-type-radio {
  display: flex;
  flex-wrap: wrap;
}

.radio-group-item {
  flex: 1;
  min-width: 300px;
}

/* 鎸夐挳缁勬牱寮?- 鍏抽敭淇敼锛氫繚鎸佸師鏈夊竷灞€ */
.button-group-item {
  margin-left: 8px !important; /* 浠呬繚鐣欏皯閲忛棿璺濓紝涓嶄娇鐢╝uto */
  margin-right: 0 !important;
  display: flex !important;
  align-items: center !important;
}

.button-group {
  display: flex;
  gap: 12px;
}

.query-button,
.sync-button {
  min-width: 100px;
}

/* 鏍稿績淇锛氭搷浣滆甯冨眬 - 鍏抽敭淇敼 */
.form-actions-row {
  display: flex;
  align-items: center;
  justify-content: flex-start;
  width: 100%;
  min-height: 40px;
  box-sizing: border-box;
  /* 绉婚櫎涔嬪墠鐨刾adding-right锛岄伩鍏嶅奖鍝嶅叾浠栨寜閽?*/
  padding-right: 0 !important;
}

/* 宸插垹闄ゆ寜閽鍣?- 鐙珛瀹氫綅锛屼笉褰卞搷鍏朵粬鎸夐挳 */
.delete-btn-2-wrapper {
  margin-left: auto !important; /* 鑷姩闈犲彸锛屼笉褰卞搷宸︿晶鎸夐挳 */
  margin-right: 0 !important;
  padding: 0 !important;
  width: 100px !important;
  height: 32px !important;
  display: flex !important;
  align-items: center !important;
  justify-content: center !important;
}

/* 鍝嶅簲寮忚皟鏁达細灞忓箷杈冨皬鏃跺厑璁镐富鏌ヨ琛屾崲琛?*/
@media (max-width: 1440px) {
  .form-main-row {
    flex-wrap: wrap; /* 鍏佽鎹㈣ */
  }
  .form-item-date,
  .form-item-input {
    margin-bottom: 12px !important; /* 鎹㈣鍚庢坊鍔犲簳閮ㄩ棿璺?*/
  }
}

@media (max-width: 1200px) {
  .form-actions-row {
    flex-wrap: wrap; /* 鍏佽鍏朵粬鍏冪礌鎹㈣ */
    min-height: 60px; /* 澧炲ぇ琛岄珮 */
  }
  .batch-operation-item {
    margin-left: 20px !important;
    margin-top: 8px !important;
  }
  /* 鍝嶅簲寮忎笅鎸夐挳缁勮皟鏁?*/
  .button-group-item {
    margin-left: 20px !important;
    margin-top: 8px !important;
  }
  /* 宸插垹闄ゆ寜閽湪灏忓睆骞曚笅鎹㈣鏄剧ず */
  .delete-btn-2-wrapper {
    margin-left: 20px !important;
    margin-top: 8px !important;
    margin-right: 0 !important;
    width: auto !important;
  }
}

@media (max-width: 992px) {
  .form-item {
    margin-right: 16px !important;
  }
}

@media (max-width: 768px) {
  .form-item-date,
  .form-item-input {
    flex: 1 1 100%; /* 鍗犳弧鏁磋 */
    min-width: unset;
  }
  .button-group {
    width: 100%;
    justify-content: space-between;
  }
  .query-button,
  .sync-button,
  .delete-button {
    flex: 1;
    margin: 0 4px;
  }
}

/* 鍘熸湁鏍峰紡淇濇寔涓嶅彉 */
.video-container {
  position: relative;
  border-bottom: 1px solid #e8e8e8;
  overflow: hidden;
  max-height: 420px;
}

.loading-overlay {
  position: absolute;
  top: 0;
  left: 0;
  width: 100%;
  height: 100%;
  background-color: rgba(0, 0, 0, 0.7);
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  z-index: 10;
  transition: all 0.3s ease;
}

.loading-tip {
  color: #ffffff;
  font-size: 16px;
  margin-top: 20px;
  text-align: center;
  padding: 0 20px;
}

.error-container {
  position: absolute;
  top: 0;
  left: 0;
  width: 100%;
  height: 100%;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 20px;
  background-color: #fff;
}

.video-info-bar {
  padding: 16px 24px;
  background: #f8f9fa;
  border-bottom: 1px solid #e8e8e8;
}

.info-container {
  display: flex;
  gap: 40px;
  align-items: center;
  flex-wrap: wrap;
}

.info-item {
  display: flex;
  flex: 1;
  align-items: center;
  font-size: 14px;
  line-height: 1.6;
  flex-wrap: nowrap;
}

.info-label {
  color: #666666;
  margin-right: 8px;
  white-space: nowrap;
  font-weight: 500;
}

.info-value {
  color: #333333;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  margin-right: 8px;
}

/* 鏂板锛氬鍒惰矾寰勬寜閽牱寮?*/
.copy-path-btn {
  padding: 0 6px !important;
  height: 24px !important;
  font-size: 12px !important;
  white-space: nowrap;
}

.video-title-link {
  color: #1890ff;
  cursor: pointer;
  text-decoration: none;
  display: inline-block;
  max-width: 100%;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

:deep(.ant-modal-title) {
  font-size: 16px !important;
  font-weight: 500 !important;
  color: #1f2937 !important;
  line-height: 1.5 !important;
  white-space: nowrap !important;
  overflow: hidden !important;
  text-overflow: ellipsis !important;
  max-width: calc(100% - 40px) !important;
}

:deep(.ant-modal) {
  border-radius: 8px !important;
  box-shadow: 0 6px 30px rgba(0, 0, 0, 0.1) !important;
  overflow: hidden !important;
  max-width: 85vw !important;
  max-height: 80vh !important;
  min-width: 500px !important;
  min-height: 380px !important;
  width: 900px !important;
}

:deep(.ant-modal-header) {
  border-bottom: 1px solid #e8e8e8 !important;
  padding: 16px 24px !important;
  border-radius: 8px 8px 0 0 !important;
  background-color: #fff !important;
  display: flex !important;
  align-items: center !important;
  justify-content: space-between !important;
}

:deep(.ant-modal-close) {
  color: #8c8c8c !important;
  transition: all 0.2s ease !important;
  width: 40px !important;
  height: 40px !important;
  border-radius: 50% !important;
  flex-shrink: 0 !important;
}

:deep(.ant-modal-close:hover) {
  color: #1890ff !important;
  background-color: #f0f9ff !important;
}

:deep(.ant-modal-content) {
  border-radius: 8px !important;
  overflow: hidden !important;
}

:deep(.ant-modal-mask) {
  background-color: rgba(0, 0, 0, 0.5) !important;
  backdrop-filter: blur(2px) !important;
}

:deep(.ant-spin-dot) {
  color: #1890ff !important;
  font-size: 36px !important;
}

:deep(.ant-spin-tip) {
  color: #ffffff !important;
  font-size: 16px !important;
  margin-top: 20px !important;
}

:deep(.ant-alert-error) {
  border: none !important;
  background-color: #fff2f0 !important;
  color: #ff4d4f !important;
  padding: 12px 16px !important;
  width: 100%;
  max-width: 600px;
}

:deep(.ant-alert-icon) {
  color: #ff4d4f !important;
  font-size: 16px !important;
  margin-right: 8px !important;
}

/* 鏂板锛氳〃鏍煎閫夋鍒楁牱寮忚皟鏁?*/
:deep(.ant-table-selection-column) {
  width: 50px !important;
  text-align: center !important;
}

/* 鏂板锛氭搷浣滃垪鎸夐挳鏍峰紡 */
:deep(.ant-space-item button) {
  padding: 0 8px !important;
  height: 28px !important;
  font-size: 13px !important;
}

@media (max-width: 1200px) {
  .video-element {
    max-height: 380px;
  }
}

@media (max-width: 768px) {
  .video-element {
    max-height: 300px;
  }
  .info-container {
    gap: 20px;
  }
  :deep(.ant-modal) {
    width: 95% !important;
    min-width: 320px !important;
    min-height: 320px !important;
  }
  :deep(.ant-modal-title) {
    max-width: calc(100% - 30px) !important;
    font-size: 15px !important;
  }
  :deep(.ant-spin-dot) {
    font-size: 28px !important;
  }
  .loading-tip {
    font-size: 14px;
  }
  /* 鍝嶅簲寮忎笅鎿嶄綔鍒楄皟鏁?*/
  :deep(.ant-table-column-has-fix-right) {
    right: 0 !important;
  }
}

@media (max-width: 480px) {
  .video-element {
    min-height: 220px;
  }
  .video-info-bar {
    padding: 12px 16px;
  }
  .info-container {
    gap: 12px;
    flex-direction: column;
    align-items: flex-start;
  }
  :deep(.ant-modal-title) {
    max-width: calc(100% - 25px) !important;
    font-size: 14px !important;
  }
  /* 绉诲姩绔搷浣滃垪鎹㈣鏄剧ず */
  :deep(.ant-space) {
    flex-direction: column !important;
    align-items: flex-start !important;
    gap: 4px !important;
  }
}
/* 寮圭獥鏍囬鎮仠鏍峰紡 */
.modal-title-with-tooltip {
  position: relative;
  cursor: help; /* 榧犳爣鍙樹负甯姪鍥炬爣锛屾彁绀哄彲鎮仠 */
  padding: 2px 0;
}

/* 鍙€夛細娣诲姞涓嬪垝绾垮姩鐢诲寮轰氦浜掓彁绀?*/
.modal-title-with-tooltip:hover {
  text-decoration: underline;
  text-underline-offset: 4px;
  text-decoration-color: #1890ff;
  text-decoration-thickness: 1px;
}
/* 宸插垹闄よ棰戞娊灞?- 鍒楄〃瀹瑰櫒鍩虹鏍峰紡 */
:deep(.ant-drawer-body) {
  padding: 16px !important;
  overflow-y: auto;
}

:deep(.ant-list) {
  margin: 0 !important;
}

/* 宸插垹闄よ棰?- 鍒楄〃椤瑰竷灞€浼樺寲 */
:deep(.ant-list-item) {
  display: flex !important;
  align-items: center !important;
  justify-content: space-between !important;
  padding: 12px 16px !important;
  border-bottom: 1px solid #f0f0f0 !important;
  transition: background-color 0.2s ease;
}

/* 鍒楄〃椤规偓鍋滄晥鏋滐紝澧炲己浜や簰鎰?*/
:deep(.ant-list-item:hover) {
  background-color: #f8f9fa !important;
}

/* 宸插垹闄よ棰?- 鏍囬瀹瑰櫒锛堟牳蹇冿細瀹炵幇鍗曡鐪佺暐锛?*/
.delete-video-title-container {
  display: flex;
  align-items: center;
  flex: 1; /* 鍗犳弧宸︿晶鍓╀綑绌洪棿锛岄檺鍒舵枃鏈搴?*/
  margin-right: 16px; /* 涓庡鍒舵寜閽繚鎸侀棿璺?*/
  overflow: hidden; /* 闅愯棌婧㈠嚭鍐呭 */
}

/* 搴忓彿鏍峰紡 */
.delete-video-index {
  color: #666;
  margin-right: 8px;
  flex: 0 0 auto; /* 搴忓彿涓嶆敹缂┿€佷笉鏀惧ぇ锛屽浐瀹氬搴?*/
  white-space: nowrap;
}

/* 瑙嗛鏍囬锛堟牳蹇冿細鍗曡鏂囨湰婧㈠嚭鐪佺暐锛?*/
.delete-video-title {
  flex: 1; /* 鍗犳弧瀹瑰櫒鍓╀綑绌洪棿锛岃Е鍙戝搴﹂檺鍒?*/
  white-space: nowrap; /* 寮哄埗鏂囨湰鍗曡鏄剧ず */
  overflow: hidden; /* 闅愯棌婧㈠嚭鐨勬枃鏈?*/
  text-overflow: ellipsis; /* 婧㈠嚭閮ㄥ垎鏄剧ず鐪佺暐鍙?.. */
  color: #333;
  font-size: 14px;
  line-height: 1.5;
}

/* 澶嶅埗鎸夐挳鏍峰紡浼樺寲 */
.copy-delete-video-btn {
  padding: 0 8px !important;
  height: 28px !important;
  font-size: 12px !important;
  color: #1890ff !important;
  flex: 0 0 auto; /* 鎸夐挳涓嶆敹缂┿€佷笉鏀惧ぇ锛屽浐瀹氬搴?*/
}

.copy-delete-video-btn:hover {
  color: #40a9ff !important;
  background-color: #f0f9ff !important;
  border-radius: 4px !important;
}

/* 鍙€夛細閫傞厤绉诲姩绔紝浼樺寲灏忓睆骞曟樉绀?*/
@media (max-width: 768px) {
  .delete-video-title-container {
    margin-right: 12px;
  }

  .delete-video-title {
    font-size: 13px;
  }

  .copy-delete-video-btn {
    padding: 0 6px !important;
    height: 24px !important;
  }
}

/* 馃搶 鏂板锛氬崥涓诲垪鎺掑簭鍥炬爣鏍峰紡浼樺寲锛堝拰鍙戝竷鏃堕棿鍒椾繚鎸佷竴鑷达級 */
:deep(.ant-table-column-title[data-column-key='author']) {
  cursor: pointer;
}

:deep(.ant-table-column-title[data-column-key='author']:hover) {
  color: #1890ff !important;
}

html.dark-mode .ant-table-column-sort {
  background: #161627;
}
</style>

